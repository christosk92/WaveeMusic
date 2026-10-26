// ── Themes/Zune/Structure/Views/Video/ZuneStageVideoPopOut.cs — Zune's `Video.PopOut` (P5 Z4c) ───────────────────────────────────────────
//
// The detached window's content ("play in a separate window"), sized to THIS window's viewport and ALWAYS opaque (the hole erases the fill over
// the video rect; anything unpainted shows the desktop): the shared stage over the poster when a player exists, the poster alone otherwise, and
// the title band.
//
// THE BAND IS AN OS CAPTION REGION (WM_NCHITTEST -> HTCAPTION), which the core pushes (`InputHooks.SetTitleBarRegions`) at the height
// `Video.PopOutBand.HeightDip`: the band's own layout height MUST match it or the region and the visible band drift apart. DefWindowProc owns
// the press from down to up (the OS move loop, Aero Snap, monitor hops, touch and pen, double-click maximize); the engine still synthesizes the
// pointer's real position over the region to this tree, so the band's hover (`BandPointerMoved` / `BandPointerLeft`) and the chrome's auto-show
// keep working as over client area. The band starts BELOW the OS resize strip (`PopOut.ResizeStripDip`) so the top ~8 DIP keep answering HTTOP.
//
// The band is the window's only chrome and the only identity this chromeless window has: the title in Zune's fullscreen voice (15, the
// primary on-media ink, its own casing). Opacity is the TERMINAL (the core's `OnMedia.FadeChrome` seeds the approach, from the node it gets
// through `BandRealized`); the band is a sibling of the hole, never an ancestor of it. It rides the same idle machine as the transport.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;

using VR = Wavee.Themes.Zune.ZuneStageVideoRules;

namespace Wavee.Themes.Zune;

/// <summary>Zune's pop-out window content.</summary>
internal static class ZuneStageVideoPopOut
{
    /// <summary>The registered template of <see cref="ViewKeys.VideoPopOut"/>.</summary>
    public static Element Template(VideoPopOutProps props, in ViewContext ctx)
    {
        var size = props.Size;
        Element body = props.HasPlayer
            ? new BoxEl
            {
                Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, ClipToBounds = true, ZStack = true,
                Children =
                [
                    props.Stage(),
                    props.Join == Video.JoinVisual.Video ? new BoxEl { HitTestVisible = false } : props.Poster(props.Join),
                    // the transport over the poster: a poster waiting for the first frame covers the picture, never the controls
                    props.Strip is { } strip ? strip() : new BoxEl { HitTestVisible = false },
                    TitleBand(props),
                ],
            }
            : props.Poster(props.Join);
        return new BoxEl
        {
            Direction = 1,
            Width = Prop.Of(() => size.Value.Width),
            Height = Prop.Of(() => size.Value.Height),
            Fill = Tok.MediaLetterbox,
            Children = [body],
        };
    }

    static Element TitleBand(VideoPopOutProps props)
    {
        bool show = props.BandShown;
        return new BoxEl
        {
            Grow = 1f, Direction = 1, HitTestPassThrough = true,
            Children =
            [
                new BoxEl
                {
                    Shrink = 0f, Height = Video.PopOutBand.HeightDip, Direction = 0, AlignItems = FlexAlign.Center,
                    // Top inset is the OS's own resize strip (PopOut.ResizeStripDip): the band starts BELOW it, so
                    // the top ~8 DIP of the window keeps answering HTTOP instead of the band stealing it for a drag.
                    Padding = new Edges4(VR.BandInset + Spacing.S, Video.PopOut.ResizeStripDip, VR.BandInset, 0f), Gap = Spacing.M,
                    Gradient = Tok.ScrimTop,
                    Opacity = show ? 1f : 0f,
                    HitTestVisible = show,
                    // The move cursor still shows over the band: the engine resolves it from the synthesized NC hover and
                    // Win32 re-asserts it on WM_SETCURSOR over a caption region instead of letting DefWindowProc arrow it.
                    Cursor = show ? CursorId.SizeAll : (CursorId?)null,
                    OnPointerMoveWithin = props.BandPointerMoved,
                    OnPointerExit = props.BandPointerLeft,
                    OnRealized = props.BandRealized,
                    Children =
                    [
                        // Bound REACTIVELY: the window outlives a track change.
                        new TextEl(Prop.Of(props.Title))
                        {
                            Size = VR.TitleSize, LineHeight = VR.TitleLine, Weight = VR.TitleWeight, Color = Tok.OnMediaPrimary,
                            Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                            Shrink = 1f, MinWidth = 0f,
                        },
                    ],
                },
            ],
        };
    }
}
