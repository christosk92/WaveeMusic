// ── Themes/Zune/Structure/Views/Video/ZuneStageVideoPip.cs — Zune's `Video.Pip` (P5 Z4c) ────────────────────────────────────────────────
//
// The in-window overlay of the ONE stay-mounted presenter, whose picture the docked card, the mini player and fullscreen are GEOMETRY modes of
// (Z-Mini: the floating video, 360 x 202, 16 from the edge, over whatever else is on screen, controls appear on hover). The core keeps the
// presenter (the engine element and its keys, the placement binds, the make-before-break mount, the reservation it follows) and the mini
// player's gestures (`IVideoPipGestures`: the drag, the eight zones, the clamp and the persistence over `PipGesture` and the panel policy's
// bounds); this template draws the box those binds are put on and the chrome over the picture.
//
//   box        a ZStack [ the picture (core), the chrome of the placement ]. FLOATING gives it Zune's overlay shape (square) and the flyout
//              shadow, no ring; docked and fullscreen draw none of them. Visible is BOUND, so covering or releasing the picture re-renders
//              and remounts nothing; the three geometry binds and `FollowRect` are the engine's (docked: the box is placed over the reservation).
//   docked     a hover strip of WORDS (mini player, full screen) and a close glyph, right-aligned over a scrim fading to nothing.
//   floating   a hover strip, 32 tall (Z-Mini): the drag surface and the close glyph (28 square), inset by the corner width and the edge band
//              so neither sits under a resize zone; and the eight resize zones, a 3-row skeleton whose every non-zone cell is pass-through,
//              topmost so a zone always wins over the chrome. Corner rows are 12 tall, edge bands 6 thin; the south-east zone carries the one
//              painted pixel of the layer.
//   Compact    the chrome shows at rest and every target is 40 x 40 (`ZuneStageVideoRules.Of`).
//
// Hover chrome costs no signal and no re-render: `Opacity 0 / HoverOpacity 1` under a container that earns hover with a no-op `OnPointerExit`.
// The strips are siblings of the picture, never ancestors (the hole rule).
//
// Not drawn (no prop reaches this view): the strip's title ("Nightcall · Kavinsky") and "back to wavee", and the mini player's bottom control
// bar (the on-media transport is its own key). They are listed as gaps.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;

using VR = Wavee.Themes.Zune.ZuneStageVideoRules;

namespace Wavee.Themes.Zune;

/// <summary>Zune's in-window video overlay and its chrome.</summary>
internal static class ZuneStageVideoPip
{
    /// <summary>The registered template of <see cref="ViewKeys.VideoPip"/>.</summary>
    public static Element Template(VideoPipProps props, in ViewContext ctx)
    {
        bool floating = props.Placement == Video.SurfacePlacement.Floating;
        // The chrome's targets are the window tier's: a Compact window shows 40 x 40 buttons at rest on a 40 strip.
        var tier = VR.OfFacts(ctx.Tier());
        // Fullscreen's PAGE form (Z-Video-Compact): the picture is the page's 16:9 head under the bar, not the whole window.
        bool page = props.Placement == Video.SurfacePlacement.Fullscreen && tier.PageForm;
        var viewport = ctx.Hooks.UseContextSignal(Viewport.Size);
        float barH = tier.BarH;
        var kids = new List<Element>(4) { props.Area() };
        if (props.Placement == Video.SurfacePlacement.Docked) kids.Add(DockedStrip(props, tier));
        if (floating) { kids.Add(Chrome(props, tier)); kids.Add(ResizeBands(props.Gestures)); }

        return new BoxEl
        {
            Direction = 1, ClipToBounds = true, ZStack = true,
            // A BOUND Visible, so covering or releasing the picture re-renders and remounts nothing: the subtree goes inactive, the element
            // hides its DComp visual and pumps with an inert binding (nothing is placed or shown from this slot; the player's own state pump
            // never depended on this element), and uncovering re-places the same visual with no new slot.
            Visible = props.Visible,
            // Docked: the engine places this box over the reservation's painted rect in the same frame and the three binds below
            // stand down; every other placement is driven by them.
            FollowRect = props.FollowRect,
            Width = page ? Prop.Of(() => viewport.Value.Width) : props.Width,
            Height = page ? Prop.Of(() => viewport.Value.Width * VR.PageAspect) : props.Height,
            Transform = page ? (Prop<Affine2D>)Affine2D.Translation(0f, barH) : props.Transform,
            Fill = ColorF.Transparent,
            Corners = floating ? R.Shape.All(ShapeRole.Overlay) : default,
            Shadow = floating ? Elevation.Flyout : default,
            OnPointerExit = static () => { },
            Children = kids.ToArray(),
        };
    }

    // ── the docked strip ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The hover-revealed top strip: "mini player" and "full screen" as words, then a close glyph, right-aligned (shown at rest on a Compact window).</summary>
    static Element DockedStrip(VideoPipProps props, ZuneStageVideoLayout tier) => new BoxEl
    {
        Grow = 1f, Direction = 1, HitTestPassThrough = true,
        Children =
        [
            new BoxEl
            {
                Height = MathF.Max(tier.WordH, tier.GlyphBox), Shrink = 0f, Direction = 0,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.End, Gap = VR.StripGap,
                Padding = new Edges4(VR.StripPadX, 0f, Spacing.XXS, 0f),
                Gradient = Tok.ScrimTop,
                Opacity = tier.ChromeAtRest ? 1f : 0f, HoverOpacity = 1f,
                HoverDurationMs = Design.Motion.Fast, HoverEasing = Easing.FluentDecelerate,
                Children =
                [
                    Word(Loc.Get(Strings.Theme.Zune.Stage.VideoMiniPlayerWord), props.ToMiniPlayer, tier.WordH),
                    Word(Loc.Get(Strings.Player.VideoFullScreen), props.ToFullscreen, tier.WordH),
                    Glyph(Icons.Cancel, Loc.Get(Strings.Player.TurnOffVideo), props.TurnOffDocked, tier),
                ],
            },
        ],
    };

    /// <summary>A word of the docked strip: 13/18, lowercase through the command case, the on-media secondary ink lifting to the primary on hover.
    /// The word is its own name.</summary>
    static Element Word(string text, Action onClick, float target) => new BoxEl
    {
        Height = target, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center,
        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand, OnClick = onClick,
        Children = [new TextEl(text) { Size = VR.WordSize, LineHeight = VR.WordLine, Color = Tok.OnMediaSecondary, HoverColor = Tok.OnMediaPrimary, Case = R.Case.For(LabelKind.Command), MaxLines = 1 }],
    };

    /// <summary>A hover-revealed chrome glyph on the on-media ladder (28 x 28, 40 x 40 on a Compact window). Its tooltip is its name, and names
    /// the DESTINATION.</summary>
    static Element Glyph(string glyph, string tip, Action onClick, ZuneStageVideoLayout tier) => ToolTip.Wrap(new BoxEl
    {
        Width = tier.GlyphBox, Height = tier.GlyphBox, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = R.Shape.All(ShapeRole.Control),
        Fill = ColorF.Transparent,
        HoverFill = Tok.OnMediaPrimary with { A = VR.GlyphHoverAlpha },
        PressedFill = Tok.OnMediaPrimary with { A = VR.GlyphPressedAlpha },
        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false,
        Cursor = CursorId.Hand, OnClick = onClick,
        Children = [new TextEl(glyph) { Size = VR.GlyphSize, FontFamily = Tok.Definition.Fonts.Icon, Color = Tok.OnMediaSecondary, HoverColor = Tok.OnMediaPrimary }],
    }, tip);

    // ── the mini player's chrome ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The hover strip: a drag surface and the close glyph, inset by the corner width and the edge band so neither sits under a resize
    /// zone. On a Compact window it shows at rest, 40 x 40 targets, with full screen at its leading end.</summary>
    static Element Chrome(VideoPipProps props, ZuneStageVideoLayout tier)
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
        var close = Glyph(Icons.Cancel, Loc.Get(Strings.Player.TurnOffVideo), props.ClosePip, tier);
        var strip = new BoxEl
        {
            Height = MathF.Max(tier.StripH, tier.GlyphBox + Video.Pip.EdgeBand), Shrink = 0f, Direction = 0,
            Padding = new Edges4(Video.Pip.CornerW, Video.Pip.EdgeBand, Video.Pip.CornerW, 0f),
            Gradient = Tok.ScrimTop,
            Opacity = tier.ChromeAtRest ? 1f : 0f, HoverOpacity = 1f,
            HoverDurationMs = Design.Motion.Fast, HoverEasing = Easing.FluentDecelerate,
            Children = tier.ChromeAtRest
                ? [Glyph(Icons.FullScreen, Loc.Get(Strings.Player.VideoFullScreen), props.ToFullscreen, tier), dragSurface, close]
                : [dragSurface, close],
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
                                    Width = VR.GripMark, Height = VR.GripMark,
                                    Margin = new Edges4(0f, 0f, VR.GripMarkInset, VR.GripMarkInset), HitTestVisible = false,
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
