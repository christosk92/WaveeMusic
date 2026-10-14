// ── Themes/Classic/Structure/Views/Video/ClassicVideoPopOut.cs — Classic's `Video.PopOut` (themes plan 6.1, T7c) ──────────────────
//
// From `Shell/Video.UI.cs` (`PopOutContent`'s tree and `TitleBand`): the detached window's content, sized to THIS window's viewport and
// ALWAYS opaque (the hole erases the fill over the video rect; anything unpainted shows the desktop): the shared stage over the poster when a
// player exists, the poster alone otherwise (0.2.9 left a bare letterbox rect here, W14b: a deliberate divergence), and the title band.
//
// THE BAND IS AN OS CAPTION REGION (WM_NCHITTEST -> HTCAPTION), which the core pushes (`InputHooks.SetTitleBarRegions`) at the height
// `Video.PopOutBand.HeightDip`: the band's own layout height MUST match it or the region and the visible band drift apart. DefWindowProc owns
// the press from down to up (the OS move loop, Aero Snap, monitor hops, touch and pen, double-click maximize); the engine still
// synthesizes the pointer's real position over the region to this tree, so the band's hover (`BandPointerMoved` / `BandPointerLeft`) and
// the chrome's auto-show keep working as over client area. The band starts BELOW the OS resize strip (`PopOut.ResizeStripDip`) so the top
// ~8 DIP keep answering HTTOP instead of the band stealing them for a drag.
//
// The band is the window's only chrome and the only identity this chromeless window has, and the visible answer to "can I drag this?".
// Opacity is the TERMINAL (the core's `OnMedia.FadeChrome` seeds the approach, from the node it gets through `BandRealized`); the band is a
// sibling of the hole, never an ancestor of it. It rides the same idle machine as the transport: move the pointer and both appear, idle
// and both go with the cursor.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's pop-out window content.</summary>
internal static class ClassicVideoPopOut
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
                    Padding = new Edges4(ClassicVideoRules.BandInset + Spacing.S, Video.PopOut.ResizeStripDip, ClassicVideoRules.BandInset, 0f), Gap = Spacing.M,
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
                            Size = ClassicVideoRules.TitleSize, Weight = 600, Color = Tok.OnMediaPrimary,
                            Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                            Shrink = 1f, MinWidth = 0f,
                        },
                    ],
                },
            ],
        };
    }
}
