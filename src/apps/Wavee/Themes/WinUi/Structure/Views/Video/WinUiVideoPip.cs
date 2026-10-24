// ── Themes/WinUi/Structure/Views/Video/WinUiVideoPip.cs — WinUI's `Video.Pip` (audit C0; boards W-Video, -Small, -Compact) ─────────────────
//
// The in-window overlay of the ONE stay-mounted presenter, whose picture the docked card, the mini player and fullscreen are GEOMETRY modes of. The
// core keeps the presenter (the engine element and its keys, the placement binds, the make-before-break mount, the reservation it follows) and the
// mini player's gestures (`IVideoPipGestures`: the drag, the zones, the clamp and the persistence over `PipGesture` and the panel policy's bounds);
// this template draws the box those binds are put on and the chrome over the picture.
//
//   box        a ZStack [ the picture (core), the chrome of the placement ]. FLOATING (W-Video: "floating mini player over the shell") gives it the
//              overlay's r8 corners, a one-DIP surface ring and the flyout shadow; docked and fullscreen draw none of them. The card floats OVER
//              the page (`WinUiPlayerTiers.Panel`: `PipBounds.OverlaysContent`), so the page paints to the player bar under it. Visible is BOUND,
//              so covering or releasing the picture re-renders and remounts nothing.
//   docked     a hover strip (mini player, full screen, turn off video), right-aligned over the top scrim; at rest on a Compact window.
//   floating   the top strip: the drag surface, then the window's placement button ("Dock in rail" where the rail fits, W-Video; else "Play in a
//              separate window", W-Video-Small), full screen and close, each keyed, 32 square (40 at Compact, at rest: W-Video-Compact). Then the
//              resize zones, topmost so a zone always wins over the chrome: the core's eight above Compact (12 x 14 corners, 6 bands; the south-
//              east one carries the grip, an L of two strokes revealed with the chrome); at Compact the card's four edges, full length, a press
//              near a bar's end gripping the corner too (no corner cell under the 40 touch floor, nothing over the transport's ends).
//
// Hover chrome costs no signal and no re-render: `Opacity 0 / HoverOpacity 1` under a container that earns hover with a no-op `OnPointerExit`.
// The strips are siblings of the picture, never ancestors (the hole rule). The on-media transport over the picture is its own key.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;

using VR = Wavee.Themes.WinUi.WinUiVideoRules;

namespace Wavee.Themes.WinUi;

/// <summary>WinUI's in-window video overlay and its chrome.</summary>
internal static class WinUiVideoPip
{
    /// <summary>The overlay's key; its parts are keyed under it (<c>winui.video.pip.close</c>, ...).</summary>
    public const string Key = "winui.video.pip";

    /// <summary>The registered template of <see cref="ViewKeys.VideoPip"/>.</summary>
    public static Element Template(VideoPipProps props, in ViewContext ctx)
    {
        bool floating = props.Placement == Video.SurfacePlacement.Floating;
        var tier = VR.OfFacts(ctx.Tier());
        var kids = new List<Element>(4) { props.Area() };
        if (props.Placement == Video.SurfacePlacement.Docked) kids.Add(DockedStrip(props, tier));
        if (floating)
        {
            kids.Add(Chrome(props, tier));
            kids.Add(tier.EdgeZones ? EdgeZones(props.Gestures, tier) : ResizeZones(props.Gestures, tier));
        }

        return new BoxEl
        {
            Key = Key,
            Direction = 1, ClipToBounds = true, ZStack = true,
            Visible = props.Visible,
            // Docked: the engine places this box over the reservation's painted rect in the same frame and the three binds below stand down.
            FollowRect = props.FollowRect,
            Width = props.Width,
            Height = props.Height,
            Transform = props.Transform,
            Fill = ColorF.Transparent,
            Corners = floating ? Radii.OverlayAll : default,
            BorderWidth = floating ? VR.RingWidth : 0f,
            BorderColor = floating ? Prop.Of(static () => Tok.StrokeSurfaceDefault) : ColorF.Transparent,
            Shadow = floating ? Elevation.Flyout : default,
            OnPointerExit = static () => { },
            Children = kids.ToArray(),
        };
    }

    // ── the chrome's buttons ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A chrome button on the on-media ladder: the tier's square target, its icon, a plate on hover; its tooltip names the DESTINATION.</summary>
    static Element Glyph(string key, string glyph, string tip, Action onClick, WinUiVideoPipLayout tier) => ToolTip.Wrap(new BoxEl
    {
        Key = Key + "." + key,
        Width = tier.GlyphBox, Height = tier.GlyphBox, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = Radii.ControlAll,
        Fill = ColorF.Transparent,
        HoverFill = Tok.OnMediaPrimary with { A = VR.GlyphHoverAlpha },
        PressedFill = Tok.OnMediaPrimary with { A = VR.GlyphPressedAlpha },
        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false,
        Cursor = CursorId.Hand, OnClick = onClick,
        Children = [new TextEl(glyph) { Size = tier.Glyph, FontFamily = Tok.Definition.Fonts.Icon, Color = Tok.OnMediaSecondary, HoverColor = Tok.OnMediaPrimary }],
    }, tip);

    /// <summary>The hover-revealed (at rest on a Compact window) strip of buttons over the top scrim.</summary>
    static BoxEl Strip(WinUiVideoPipLayout tier, Edges4 padding, CornerRadius4 corners, Element[] children) => new()
    {
        Height = tier.GlyphBox + padding.Top, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Gap = VR.GlyphGap,
        Padding = padding,
        Gradient = Tok.ScrimTop,
        Corners = corners,
        Opacity = tier.ChromeAtRest ? 1f : 0f, HoverOpacity = 1f,
        HoverDurationMs = Design.Motion.Fast, HoverEasing = Easing.FluentDecelerate,
        Children = children,
    };

    // ── the docked strip ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The docked card's strip: mini player, full screen, turn off video, right-aligned.</summary>
    static Element DockedStrip(VideoPipProps props, WinUiVideoPipLayout tier) => new BoxEl
    {
        Grow = 1f, Direction = 1, HitTestPassThrough = true,
        Children =
        [
            Strip(tier, new Edges4(Spacing.S, Video.Pip.EdgeBand, Spacing.S, 0f), default,
            [
                new BoxEl { Grow = 1f, MinWidth = 0f, HitTestPassThrough = true },
                Glyph("mini", Icons.BackToWindow, Loc.Get(Strings.Player.VideoMiniPlayer), props.ToMiniPlayer, tier),
                Glyph("fullscreen", Icons.FullScreen, Loc.Get(Strings.Player.VideoFullScreen), props.ToFullscreen, tier),
                Glyph("off", Icons.Cancel, Loc.Get(Strings.Player.TurnOffVideo), props.TurnOffDocked, tier),
            ]),
        ],
    };

    // ── the mini player's chrome ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The top strip: the drag surface, then the placement button, full screen and close; inset by the zones so none sits under one.</summary>
    static Element Chrome(VideoPipProps props, WinUiVideoPipLayout tier)
    {
        var gestures = props.Gestures;
        var drag = new BoxEl
        {
            Key = Key + ".drag",
            Grow = 1f, MinWidth = 0f, Height = tier.GlyphBox,
            Cursor = CursorId.SizeAll,
            OnRealized = gestures.DragRealized,
            OnPointerDown = gestures.DragDown,
            OnDrag = gestures.DragMove,
            OnClick = gestures.Commit,          // an OnDrag node's click IS its release edge
            OnDragCanceled = gestures.Commit,
        };
        var kids = new List<Element>(4) { drag };
        if (props.ToDocked is { } dock) kids.Add(Glyph("dock", VR.DockGlyph, Loc.Get(Strings.Player.DockInRail), dock, tier));
        else if (props.ToSeparateWindow is { } window) kids.Add(Glyph("window", Icons.OpenInNewWindow, Loc.Get(Strings.Player.VideoInSeparateWindow), window, tier));
        kids.Add(Glyph("fullscreen", Icons.FullScreen, Loc.Get(Strings.Player.VideoFullScreen), props.ToFullscreen, tier));
        kids.Add(Glyph("close", Icons.Cancel, Loc.Get(Strings.Player.TurnOffVideo), props.ClosePip, tier));

        float side = tier.EdgeZones ? Video.Pip.EdgeBand : Video.Pip.CornerW;
        var top = new CornerRadius4(Radii.Overlay, Radii.Overlay, 0f, 0f);
        return new BoxEl
        {
            Grow = 1f, Direction = 1, HitTestPassThrough = true,
            Children = [Strip(tier, new Edges4(side, Video.Pip.EdgeBand, side, 0f), top, kids.ToArray())],
        };
    }

    /// <summary>The grip: an L of two strokes in the south-east corner (W-Video), painted only, revealed with the chrome.</summary>
    static BoxEl Grip(WinUiVideoPipLayout tier) => new()
    {
        Width = VR.GripLength, Height = VR.GripLength, Shrink = 0f, ZStack = true, HitTestVisible = false,
        Margin = new Edges4(0f, 0f, VR.GripInset, VR.GripInset),
        Opacity = tier.ChromeAtRest ? 1f : 0f, HoverOpacity = 1f,
        HoverDurationMs = Design.Motion.Fast, HoverEasing = Easing.FluentDecelerate,
        Children =
        [
            new BoxEl { Width = VR.GripLength, Height = VR.GripStroke, AlignSelf = FlexAlign.End, Fill = Tok.OnMediaTertiary },
            new BoxEl { Width = VR.GripStroke, Height = VR.GripLength, JustifySelf = FlexAlign.End, Fill = Tok.OnMediaTertiary },
        ],
    };

    /// <summary>One resize zone: its slot's node, press and drag (the edges it grips are fixed).</summary>
    static BoxEl Zone(IVideoPipGestures gestures, int slot, Video.PipEdge edge, CursorId cursor) => new()
    {
        Key = Key + ".zone:" + slot.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Cursor = cursor,
        OnRealized = h => gestures.ZoneRealized(slot, h),
        OnPointerDown = p => gestures.ZoneDown(slot, p),
        OnDrag = p => gestures.ZoneMove(slot, edge, p),
        OnClick = gestures.Commit,
        OnDragCanceled = gestures.Commit,
    };

    /// <summary>The core's eight zones (above Compact): a 3-row skeleton whose every non-zone cell is pass-through, topmost so a zone always wins over
    /// the chrome. Corner rows are 12 tall, corners 14 wide, edge bands 6 thin; the south-east corner carries the grip.</summary>
    static Element ResizeZones(IVideoPipGestures gestures, WinUiVideoPipLayout tier) => new BoxEl
    {
        Grow = 1f, Direction = 1, HitTestPassThrough = true,
        Children =
        [
            new BoxEl
            {
                Height = Video.Pip.CornerH, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Start, HitTestPassThrough = true,
                Children =
                [
                    Zone(gestures, 0, Video.PipEdge.Left | Video.PipEdge.Top, CursorId.SizeNWSE) with { Width = Video.Pip.CornerW, Height = Video.Pip.CornerH },
                    Zone(gestures, 1, Video.PipEdge.Top, CursorId.SizeNS) with { Grow = 1f, Height = Video.Pip.EdgeBand },
                    Zone(gestures, 2, Video.PipEdge.Right | Video.PipEdge.Top, CursorId.SizeNESW) with { Width = Video.Pip.CornerW, Height = Video.Pip.CornerH },
                ],
            },
            new BoxEl
            {
                Grow = 1f, MinHeight = 0f, Direction = 0, HitTestPassThrough = true,
                Children =
                [
                    Zone(gestures, 3, Video.PipEdge.Left, CursorId.SizeWE) with { Width = Video.Pip.EdgeBand },
                    new BoxEl { Grow = 1f, MinWidth = 0f, HitTestPassThrough = true },
                    Zone(gestures, 4, Video.PipEdge.Right, CursorId.SizeWE) with { Width = Video.Pip.EdgeBand },
                ],
            },
            new BoxEl
            {
                Height = Video.Pip.CornerH, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.End, HitTestPassThrough = true,
                Children =
                [
                    Zone(gestures, 5, Video.PipEdge.Left | Video.PipEdge.Bottom, CursorId.SizeNESW) with { Width = Video.Pip.CornerW, Height = Video.Pip.CornerH },
                    Zone(gestures, 6, Video.PipEdge.Bottom, CursorId.SizeNS) with { Grow = 1f, Height = Video.Pip.EdgeBand },
                    Zone(gestures, 7, Video.PipEdge.Right | Video.PipEdge.Bottom, CursorId.SizeNWSE) with
                    {
                        Width = Video.Pip.CornerW, Height = Video.Pip.CornerH,
                        Direction = 0, Justify = FlexJustify.End, AlignItems = FlexAlign.End,
                        Children = [Grip(tier)],
                    },
                ],
            },
        ],
    };

    /// <summary>The Compact zones: the card's four edges as full-length bars (the top and bottom ones span the card, the side ones the rows between),
    /// each gripping its own edge, and a press within <see cref="WinUiVideoRules.CornerReach"/> of a bar's end gripping that corner's other edge
    /// too. The grip mark sits in the south-east corner, painted only.</summary>
    static Element EdgeZones(IVideoPipGestures gestures, WinUiVideoPipLayout tier)
    {
        BoxEl Bar(int slot, Video.PipEdge edge, CursorId cursor, bool horizontal)
        {
            var reach = new EdgeReach(edge, horizontal);
            return new BoxEl
            {
                Key = Key + ".zone:" + slot.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Cursor = cursor,
                OnBoundsChanged = reach.Measure,
                OnRealized = h => gestures.ZoneRealized(slot, h),
                OnPointerDown = p => { reach.Press(p); gestures.ZoneDown(slot, p); },
                OnDrag = p => gestures.ZoneMove(slot, reach.Edge, p),
                OnClick = gestures.Commit,
                OnDragCanceled = gestures.Commit,
            };
        }

        return new BoxEl
        {
            Grow = 1f, ZStack = true, HitTestPassThrough = true,
            Children =
            [
                new BoxEl
                {
                    Grow = 1f, Direction = 1, HitTestPassThrough = true,
                    Children =
                    [
                        Bar(1, Video.PipEdge.Top, CursorId.SizeNS, horizontal: true) with { Height = Video.Pip.EdgeBand, Shrink = 0f },
                        new BoxEl
                        {
                            Grow = 1f, MinHeight = 0f, Direction = 0, HitTestPassThrough = true,
                            Children =
                            [
                                Bar(3, Video.PipEdge.Left, CursorId.SizeWE, horizontal: false) with { Width = Video.Pip.EdgeBand },
                                new BoxEl { Grow = 1f, MinWidth = 0f, HitTestPassThrough = true },
                                Bar(4, Video.PipEdge.Right, CursorId.SizeWE, horizontal: false) with { Width = Video.Pip.EdgeBand },
                            ],
                        },
                        Bar(6, Video.PipEdge.Bottom, CursorId.SizeNS, horizontal: true) with { Height = Video.Pip.EdgeBand, Shrink = 0f },
                    ],
                },
                Grip(tier) with { AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.End },
            ],
        };
    }

    /// <summary>A full-length edge bar's grip: its own edge, plus the corner's other edge when the press lands within reach of either end.</summary>
    sealed class EdgeReach(Video.PipEdge own, bool horizontal)
    {
        float _length;
        Video.PipEdge _near;

        /// <summary>The edges the current press grips.</summary>
        public Video.PipEdge Edge => own | _near;

        public void Measure(RectF r) => _length = horizontal ? r.W : r.H;

        public void Press(Point2 local)
        {
            float at = horizontal ? local.X : local.Y;
            _near = at <= VR.CornerReach ? (horizontal ? Video.PipEdge.Left : Video.PipEdge.Top)
                : at >= _length - VR.CornerReach ? (horizontal ? Video.PipEdge.Right : Video.PipEdge.Bottom)
                : Video.PipEdge.None;
        }
    }
}
