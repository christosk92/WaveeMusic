// ── Themes/Zune/Structure/Views/Video/ZuneStageVideoFullscreen.cs — Zune's `Video.Fullscreen` (P5 Z4c) ────────────────────────────────────
//
// The fullscreen surface's chrome (Z-Video): the 48 bar across the top over a scrim fading to nothing, "video · full screen" at the left
// (13/18, the on-media secondary ink), the track's title in the middle (15, the primary ink), and the way out at the right as a word
// ("exit full screen"). The picture lives in the stay-mounted presenter (`Video.Pip`'s overlay fills the window); the core's surface keeps the
// mode (the OS window goes borderless-fullscreen on its monitor and restores what it found, mount = enter, unmount = exit), the focus scope,
// Escape / F at the root and the never-leave-focus-null guard. This template draws the hover band only; the opacity is on THIS band only, never
// on an ancestor of the hole. Per window tier (`ZuneStageVideoRules.Of`, ZT3): the bar is 48 (56 at Huge, Z-Video-Huge; 72 at Huge 4K),
// its padding the board's (16 / 24 / 48 / 64 / 96 on the left), its type 13 (14, 17) and its words the tier's target tall (40 at Compact).
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
                    Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f, Height = MathF.Max(t.BarH, t.WordH), Gap = t.BarGap,
                    Padding = new Edges4(t.BarPadL, 0f, t.BarPadR, 0f),
                    Gradient = Tok.ScrimTop,
                    Opacity = t.ChromeAtRest ? 1f : 0f, HoverOpacity = 1f,
                    HoverDurationMs = Design.Motion.Fast, HoverEasing = Easing.FluentDecelerate,
                    Children =
                    [
                        new TextEl(Loc.Get(Strings.Theme.Zune.Stage.VideoFullScreen))
                        {
                            Size = t.LabelSize, LineHeight = t.LabelLine, Color = Tok.OnMediaSecondary, Case = R.Case.For(LabelKind.Label),
                            Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
                        },
                        new BoxEl { Grow = 1f, MinWidth = 0f },
                        // Bound REACTIVELY: the surface outlives a track change. The title keeps its own casing; the kind and the artists follow it in the secondary ink ("Nightcall · official
                        // video · Kavinsky").
                        new BoxEl
                        {
                            Direction = 0, AlignItems = FlexAlign.Center, Gap = VR.TitleGap, Shrink = 1f, MinWidth = 0f,
                            Children =
                            [
                                new TextEl(Prop.Of(props.Title))
                                {
                                    Size = t.TitleSize, LineHeight = t.TitleLine, Weight = VR.TitleWeight, Color = Tok.OnMediaPrimary,
                                    Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f,
                                },
                                new TextEl(Prop.Of(() => props.Title().Length == 0 ? "" : "·  " + Loc.Get(Strings.Theme.Zune.Stage.VideoKind)))
                                {
                                    Size = t.LabelSize, LineHeight = t.LabelLine, Color = Tok.OnMediaSecondary, Case = R.Case.For(LabelKind.Label),
                                    Wrap = TextWrap.NoWrap, MaxLines = 1, Shrink = 0f,
                                },
                                // The artists keep their own casing (names are not re-cased).
                                new TextEl(Prop.Of(() => props.Title().Length == 0 || props.Artists().Length == 0 ? "" : "·  " + props.Artists()))
                                {
                                    Size = t.LabelSize, LineHeight = t.LabelLine, Color = Tok.OnMediaSecondary, Case = TextCase.AsWritten,
                                    Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f,
                                },
                            ],
                        },
                        new BoxEl { Grow = 1f, MinWidth = 0f },
                        props.SwitchToAudio is { } toAudio ? SecondaryWord(Loc.Get(Strings.Theme.Zune.Stage.VideoSwitchToAudio), toAudio, t) : new BoxEl { Width = 0f },
                        Word(Loc.Get(Strings.Player.VideoExitFullScreen), props.Exit, t),
                    ],
                },
            ],
        };
    }

    /// <summary>"Switch to audio": a word in the on-media secondary ink, beside the way out.</summary>
    static Element SecondaryWord(string text, Action onClick, ZuneStageVideoLayout t) => new BoxEl
    {
        Height = t.WordH, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Margin = new Edges4(0f, 0f, VR.WordGap, 0f),
        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand, OnClick = onClick,
        Children = [new TextEl(text) { Size = t.LabelSize, LineHeight = t.LabelLine, Color = Tok.OnMediaSecondary, HoverColor = Tok.OnMediaPrimary, Case = R.Case.For(LabelKind.Command), MaxLines = 1 }],
    };

    /// <summary>The way out: a word, the on-media primary ink, no plate (the hover lifts nothing: it is already the brightest ink).</summary>
    static Element Word(string text, Action onClick, ZuneStageVideoLayout t) => new BoxEl
    {
        Height = t.WordH, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center,
        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand, OnClick = onClick,
        Children = [new TextEl(text) { Size = t.LabelSize, LineHeight = t.LabelLine, Color = Tok.OnMediaPrimary, Case = R.Case.For(LabelKind.Command), MaxLines = 1 }],
    };
}
