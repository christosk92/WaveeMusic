// ── Themes/Classic/Structure/Views/Video/ClassicVideoDocked.cs — Classic's `Video.Docked` (themes plan 6.1, T7c) ───────────────
//
// From `Shell/Video.UI.cs` (`DockedSurface`'s card and `VideoArea`, and `WatchStage`'s frame): the docked music-video surface, video that
// simply LIVES in the app at zero commitment. Two faces, one card: the rail's Cap (its height follows the content's own aspect at the rail's
// width and the splitter overrides it; the panel places it, the card has NO transition, NO shadow, NO corners of its own) and the watch
// page's PageStage (the page owns the envelope, so the card grows into it).
//
// THE CARD IS A HOLLOW RESERVATION. The picture is not in it: the stay-mounted presenter (`Video.Pip`) FOLLOWS this box through the
// engine's `FollowRect` (resolved after layout and the animation tick, so the picture lands on the reservation's painted rect in the same
// frame, rail slide included). The core hosts what that needs (the realized node, the last laid-out rect, the mount decision, the fit and the
// log lines, `ReportLive`); the card places the box and calls them. Rule 1 of the surfaces: no layout transition, no opacity, no offscreen
// effect and no scale Enter/Exit on ANY ancestor of the hole.
//
// THE WATCH PAGE'S FRAME is mounted OUTSIDE the page's section wrapper, skeleton region and scroll view (rule 1): a plain COLUMN carries the
// 16:9 (an aspect ratio never reaches a ZStack's measure) and a ZStack is its child: the poster ground (byte-identical to the card's, so the
// idle -> live hand-over is ONE dissolve), the idle play disc while the card is not live, and the card itself while it is. The two
// `Flow.Show`s, not C# `if`s: a KeepAlive-parked page cannot re-render, but the node-bound predicate still runs.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's docked video card.</summary>
internal static class ClassicVideoDocked
{
    /// <summary>The registered template of <see cref="ViewKeys.VideoDocked"/>.</summary>
    public static Element Template(VideoDockedProps props, in ViewContext ctx)
        => props.PageFrame is { } frame ? PageFrame(frame) : Card(props.Card!);

    // ── the card ─────────────────────────────────────────────────────────────────────────────────────────────────────

    static Element Card(VideoDockedCard props)
    {
        BoxEl card = new BoxEl
        {
            ZStack = true, ClipToBounds = true,
            // NO Shadow, NO Layout/Enter/Exit: a content-layer rung, and any transition here erases the hole.
            OnPointerExit = static () => { },
            OnKeyDown = e =>
            {
                if (e.KeyCode != Keys.Space) return;
                e.Handled = true;
                props.TogglePlay();
            },
            Focusable = true,
            Children = [Reservation(props)],
        };

        return props.Face == Video.DockedFace.PageStage
            ? card with { Grow = 1f, MinHeight = 0f, Fill = Tok.MediaLetterbox }
            : card with { Shrink = 0f, MinWidth = 0f, Height = props.Height, Fill = Tok.MediaLetterbox };
    }

    /// <summary>The hollow reservation: nothing is drawn in it and nothing hits it.</summary>
    static Element Reservation(VideoDockedCard props) => new BoxEl
    {
        Grow = 1f, MinHeight = 0f, ClipToBounds = true, Fill = Tok.MediaLetterbox,
        HitTestVisible = false,
        OnRealized = props.ReservationRealized,
        OnBoundsChanged = props.ReservationBounds,
    };

    // ── the watch page's in-page stage ───────────────────────────────────────────────────────────────────────────────

    static Element PageFrame(VideoPageFrame frame)
    {
        var layers = new List<Element>(3)
        {
            // ALWAYS the bottom layer, byte-identical to the card's poster ground: the idle -> live hand-over is ONE dissolve.
            ClassicVideoPoster.Ground(frame.PosterUrl),
        };
        if (frame.OnPlay is not null)
        {
            layers.Add(Flow.Show(() => !frame.Live(), new BoxEl
            {
                Key = "module-stage-cta",
                Grow = 1f, Direction = 1, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Children = [ToolTip.Wrap(Controls.PlayFab(frame.OnPlay, size: ClassicVideoRules.PlayDisc), Loc.Get(Strings.Detail.Play))],
            }));
        }
        layers.Add(Flow.Show(frame.Live, frame.Card()));

        return new BoxEl
        {
            Key = "module-stage",
            // A plain COLUMN carries the aspect (AspectRatio never reaches a ZStack's measure); the ZStack is its child.
            Shrink = 0f, Direction = 1, AspectRatio = 16f / 9f, MaxHeight = frame.MaxHeight,
            ClipToBounds = true, Fill = Tok.MediaLetterbox,
            Children = [new BoxEl { Grow = 1f, MinHeight = 0f, ZStack = true, ClipToBounds = true, Children = layers.ToArray() }],
        };
    }
}
