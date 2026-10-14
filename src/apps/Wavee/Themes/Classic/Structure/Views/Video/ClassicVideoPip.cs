// ── Themes/Classic/Structure/Views/Video/ClassicVideoPip.cs — Classic's `Video.Pip` (themes plan 6.1, T7c) ──────────────────────
//
// From `Shell/Video.UI.cs` (`PipSurface`'s box, `Chrome`, `ResizeBands`, `DockedOverlayChrome`, `ChromeGlyph`): the in-window overlay of
// the ONE stay-mounted presenter, whose picture the docked card, the mini player and fullscreen are GEOMETRY modes of. The core keeps the
// presenter (the engine element and its keys, the placement binds, the make-before-break mount, the reservation it follows) and the mini
// player's gestures (`IVideoPipGestures`: the drag, the eight zones, the clamp and the persistence over `PipGesture` and the panel
// policy's bounds); this template draws the box those binds are put on and the chrome over the picture.
//
//   box        a ZStack [ the picture (core), the chrome of the placement ]. FLOATING gives it a card's corners, a one-DIP ring and a flyout
//              shadow; docked and fullscreen draw none of them. Visible is BOUND, so covering or releasing the picture re-renders and remounts
//              nothing; the three geometry binds and `FollowRect` are the engine's (docked: the box is placed over the reservation).
//   docked     a hover strip (mini player, full screen, off), right-aligned, no label.
//   floating   a hover strip: the drag surface and the close button, inset by the corner width and the edge band so neither sits under a
//              resize zone; and the eight resize zones, a 3-row skeleton whose every non-zone cell is pass-through, topmost so a zone always
//              wins over the chrome. Corner rows are 12 tall, edge bands 6 thin; the south-east zone carries the one painted pixel of the layer.
//
// Hover chrome costs no signal and no re-render: `Opacity 0 / HoverOpacity 1` under a container that earns hover with a no-op
// `OnPointerExit`. The strips are siblings of the picture, never ancestors (the hole rule).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's in-window video overlay and its chrome.</summary>
internal static class ClassicVideoPip
{
    /// <summary>The registered template of <see cref="ViewKeys.VideoPip"/>.</summary>
    public static Element Template(VideoPipProps props, in ViewContext ctx)
    {
        bool floating = props.Placement == Video.SurfacePlacement.Floating;
        var kids = new List<Element>(4) { props.Area() };
        if (props.Placement == Video.SurfacePlacement.Docked) kids.Add(DockedStrip(props));
        if (floating) { kids.Add(Chrome(props)); kids.Add(ResizeBands(props.Gestures)); }

        return new BoxEl
        {
            Direction = 1, ClipToBounds = true, ZStack = true,
            // A BOUND Visible, so covering or releasing the picture re-renders and remounts nothing: the subtree goes inactive, the element
            // hides its DComp visual and pumps with an inert binding (nothing is placed or shown from this slot; the player's own state pump
            // never depended on this element, F132), and uncovering re-places the same visual with no new slot.
            Visible = props.Visible,
            // Docked: the engine places this box over the reservation's painted rect in the same frame (F169) and the three binds below
            // stand down; every other placement is driven by them as before.
            FollowRect = props.FollowRect,
            Width = props.Width,
            Height = props.Height,
            Transform = props.Transform,
            Fill = ColorF.Transparent,
            Corners = floating ? CornerRadius4.All(Radii.Card) : default,
            BorderWidth = floating ? 1f : 0f,
            BorderColor = floating ? Prop.Of(static () => Tok.StrokeCardDefault) : ColorF.Transparent,
            Shadow = floating ? Elevation.Flyout : default,
            OnPointerExit = static () => { },
            Children = kids.ToArray(),
        };
    }

    // ── the docked strip ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The hover-revealed top strip: mini player, full screen, off, right-aligned, no label.</summary>
    static Element DockedStrip(VideoPipProps props) => new BoxEl
    {
        Grow = 1f, Direction = 1, HitTestPassThrough = true,
        Children =
        [
            new BoxEl
            {
                Height = ClassicVideoRules.StripH, Shrink = 0f, Direction = 0,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.End, Gap = Spacing.XXS,
                Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f),
                Gradient = Tok.ScrimTop,
                Opacity = 0f, HoverOpacity = 1f,
                HoverDurationMs = Design.Motion.Fast, HoverEasing = Easing.FluentDecelerate,
                Children =
                [
                    Glyph(Icons.BackToWindow, Loc.Get(Strings.Player.VideoMiniPlayer), props.ToMiniPlayer),
                    Glyph(Icons.FullScreen, Loc.Get(Strings.Player.VideoFullScreen), props.ToFullscreen),
                    Glyph(Icons.Cancel, Loc.Get(Strings.Player.TurnOffVideo), props.TurnOffDocked),
                ],
            },
        ],
    };

    /// <summary>A hover-revealed chrome glyph on the on-media ladder (24 x 24, 11-DIP icon). Its tooltip is its name, and names the DESTINATION.</summary>
    static Element Glyph(string glyph, string tip, Action onClick) => ToolTip.Wrap(new BoxEl
    {
        Width = ClassicVideoRules.GlyphBox, Height = ClassicVideoRules.GlyphBox, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = CornerRadius4.All(Radii.Control),
        Fill = ColorF.Transparent,
        HoverFill = Tok.OnMediaPrimary with { A = ClassicVideoRules.GlyphHoverAlpha },
        PressedFill = Tok.OnMediaPrimary with { A = ClassicVideoRules.GlyphPressedAlpha },
        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false,
        Cursor = CursorId.Hand, OnClick = onClick,
        Children = [new TextEl(glyph) { Size = ClassicVideoRules.GlyphSize, FontFamily = Tok.Definition.Fonts.Icon, Color = Tok.OnMediaSecondary, HoverColor = Tok.OnMediaPrimary }],
    }, tip);

    // ── the mini player's chrome ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The hover strip: a drag surface and the close button, inset by the corner width and the edge band so neither sits under a
    /// resize zone.</summary>
    static Element Chrome(VideoPipProps props)
    {
        var gestures = props.Gestures;
        var dragSurface = new BoxEl
        {
            Grow = 1f, MinWidth = 0f, Direction = 0, AlignItems = FlexAlign.Center,
            Cursor = CursorId.SizeAll,
            OnRealized = gestures.DragRealized,
            OnPointerDown = gestures.DragDown,
            OnDrag = gestures.DragMove,
            OnClick = gestures.Commit,          // an OnDrag node's click IS its release edge
            OnDragCanceled = gestures.Commit,
        };
        var close = Glyph(Icons.Cancel, Loc.Get(Strings.Player.TurnOffVideo), props.ClosePip);
        var strip = new BoxEl
        {
            Height = ClassicVideoRules.StripH, Shrink = 0f, Direction = 0,
            Padding = new Edges4(Video.Pip.CornerW, Video.Pip.EdgeBand, Video.Pip.CornerW, 0f),
            Gradient = Tok.ScrimTop,
            Corners = new CornerRadius4(Radii.Card, Radii.Card, 0f, 0f),
            Opacity = 0f, HoverOpacity = 1f,
            HoverDurationMs = Design.Motion.Fast, HoverEasing = Easing.FluentDecelerate,
            Children = [dragSurface, close],
        };
        return new BoxEl { Grow = 1f, Direction = 1, HitTestPassThrough = true, Children = [strip] };
    }

    /// <summary>The eight resize zones: a 3-row skeleton whose every non-zone cell is pass-through, topmost so a zone always wins over the
    /// chrome. Corner rows are 12 tall, edge bands 6 thin.</summary>
    static Element ResizeBands(IVideoPipGestures gestures)
    {
        BoxEl Band(int slot, Video.PipEdge edge, CursorId cursor) => new BoxEl
        {
            Cursor = cursor,
            OnRealized = h => gestures.ZoneRealized(slot, h),
            OnPointerDown = p => gestures.ZoneDown(slot, p),
            OnDrag = p => gestures.ZoneMove(slot, edge, p),
            OnClick = gestures.Commit,
            OnDragCanceled = gestures.Commit,
        };

        return new BoxEl
        {
            Grow = 1f, Direction = 1, HitTestPassThrough = true,
            Children =
            [
                new BoxEl
                {
                    Height = Video.Pip.CornerH, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Start, HitTestPassThrough = true,
                    Children =
                    [
                        Band(0, Video.PipEdge.Left | Video.PipEdge.Top, CursorId.SizeNWSE) with { Width = Video.Pip.CornerW, Height = Video.Pip.CornerH },
                        Band(1, Video.PipEdge.Top, CursorId.SizeNS) with { Grow = 1f, Height = Video.Pip.EdgeBand },
                        Band(2, Video.PipEdge.Right | Video.PipEdge.Top, CursorId.SizeNESW) with { Width = Video.Pip.CornerW, Height = Video.Pip.CornerH },
                    ],
                },
                new BoxEl
                {
                    Grow = 1f, MinHeight = 0f, Direction = 0, HitTestPassThrough = true,
                    Children =
                    [
                        Band(3, Video.PipEdge.Left, CursorId.SizeWE) with { Width = Video.Pip.EdgeBand },
                        new BoxEl { Grow = 1f, MinWidth = 0f, HitTestPassThrough = true },
                        Band(4, Video.PipEdge.Right, CursorId.SizeWE) with { Width = Video.Pip.EdgeBand },
                    ],
                },
                new BoxEl
                {
                    Height = Video.Pip.CornerH, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.End, HitTestPassThrough = true,
                    Children =
                    [
                        Band(5, Video.PipEdge.Left | Video.PipEdge.Bottom, CursorId.SizeNESW) with { Width = Video.Pip.CornerW, Height = Video.Pip.CornerH },
                        Band(6, Video.PipEdge.Bottom, CursorId.SizeNS) with { Grow = 1f, Height = Video.Pip.EdgeBand },
                        // The SE zone carries the one painted pixel of this layer, revealed with the chrome.
                        Band(7, Video.PipEdge.Right | Video.PipEdge.Bottom, CursorId.SizeNWSE) with
                        {
                            Width = Video.Pip.CornerW, Height = Video.Pip.CornerH,
                            Direction = 0, Justify = FlexJustify.End, AlignItems = FlexAlign.End,
                            Opacity = 0f, HoverOpacity = 1f,
                            HoverDurationMs = Design.Motion.Fast, HoverEasing = Easing.FluentDecelerate,
                            Children =
                            [
                                new BoxEl
                                {
                                    Width = ClassicVideoRules.GripMark, Height = ClassicVideoRules.GripMark,
                                    Margin = new Edges4(0f, 0f, ClassicVideoRules.GripMarkInset, ClassicVideoRules.GripMarkInset), HitTestVisible = false,
                                    Corners = new CornerRadius4(0f, 0f, Radii.Control, 0f),
                                    Fill = Tok.OnMediaTertiary,
                                },
                            ],
                        },
                    ],
                },
            ],
        };
    }
}
