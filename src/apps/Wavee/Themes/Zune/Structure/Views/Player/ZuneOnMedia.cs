// ── Themes/Zune/Structure/Views/Player/ZuneOnMedia.cs — Zune's `Player.OnMediaTransport` (P5 Z4a) ─────────────────────────────────────
//
// The strip every video overlay carries (the docked card, the mini player, the pop-out, the fullscreen layer). No board draws it: it is built
// from Zune's parts (owner Q18), the bar's language on media: over the bottom scrim, the seek row (elapsed · the 2-px line · remaining), then
// previous · the round accent disc · next on the left and mute · the volume glyph · "…" on the right, plateless, the on-media inks.
//
// It is revealed by the surface's own idle machine (`props.Visible`), never by hover (the pop-out's tree has no pointer handler above the
// stage); the pointer resting on the strip counts as chrome activity. The fade is the core's (`props.FadeChrome`), seeded after the mount.
// The "…" menu and the volume popup ask for an opaque surface over the video hole (nothing to blur there), as the engine's own transport does.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;

using O = Wavee.Themes.Zune.ZuneOnMediaLayout;

namespace Wavee.Themes.Zune;

/// <summary>Zune's on-media transport.</summary>
internal static class ZuneOnMedia
{
    /// <summary>The registered template of <see cref="ViewKeys.OnMediaTransport"/>.</summary>
    public static Element Template(OnMediaTransportProps props, in ViewContext ctx) => Embed.Comp(props, static () => new ZuneOverlayChrome());

    internal static Element Glyph(string glyph, Action onClick, bool enabled, Action<NodeHandle>? realized = null) => new BoxEl
    {
        Width = O.GlyphBox, Height = O.GlyphBox, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        PressScale = R.Motion.ScaleSubtle.PressIf(enabled),
        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, IsEnabled = enabled,
        OnClick = enabled ? onClick : null, Cursor = enabled ? CursorId.Hand : null, OnRealized = realized,
        Children =
        [
            new TextEl(glyph)
            {
                Size = O.Glyph, FontFamily = Tok.Definition.Fonts.Icon, Color = enabled ? Tok.OnMediaSecondary : Tok.OnMediaTertiary,
                HoverColor = enabled ? Tok.OnMediaPrimary : Tok.OnMediaTertiary,
            },
        ],
    };

    internal static Element Disc(Action togglePlay, bool playing, bool enabled)
    {
        string glyph = playing ? ZunePlayerLayout.PauseSolid : ZunePlayerLayout.PlaySolid;
        float solid = MathF.Round(O.DiscGlyph * ZunePlayerLayout.DiscGlyphScale);
        float nudge = playing ? 0f : MathF.Round(solid / ZunePlayerLayout.PlayNudgeDivisor);
        return new BoxEl
        {
            Width = O.Disc, Height = O.Disc, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = Radii.Circle(O.Disc),
            Fill = enabled ? Tok.AccentDefault : Tok.OnMediaTertiary, HoverFill = enabled ? Tok.AccentSecondary : Tok.OnMediaTertiary,
            PressedFill = enabled ? Tok.AccentTertiary : Tok.OnMediaTertiary, BrushTransitionMs = Design.Motion.Faster,
            HoverScale = R.Motion.ScaleEmphatic.HoverIf(enabled), PressScale = R.Motion.ScaleEmphatic.PressIf(enabled),
            Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, IsEnabled = enabled,
            OnClick = enabled ? togglePlay : null, Cursor = enabled ? CursorId.Hand : null,
            Children =
            [
                new TextEl(glyph)
                {
                    Size = solid, FontFamily = Tok.Definition.Fonts.Icon, Color = enabled ? Tok.TextOnAccentPrimary : Tok.OnMediaSecondary,
                    Margin = new Edges4(nudge, 0f, 0f, 0f),
                },
            ],
        };
    }
}

/// <summary>The strip over a video hole, revealed and concealed by the surface's idle machine.</summary>
internal sealed class ZuneOverlayChrome : Component
{
    Action<Point2>? _onPointerMoveWithin;
    Action? _onPointerExit;

    public override Element Render()
    {
        var props = UseProps<OnMediaTransportProps>();
        _onPointerMoveWithin ??= _ => props.Feed.SetPointerOverControls(true);
        _onPointerExit ??= () => props.Feed.SetPointerOverControls(false);

        bool show = props.Visible.Value;
        var facts = props.Read();
        UseSignalEffect(() => props.VolumeCell.SetIfChanged(props.Volume.Value));
        var overlay = UseContext(Overlay.Service);
        var moreAnchor = UseRef<NodeHandle>(default);
        var volAnchor = UseRef<NodeHandle>(default);
        var moreHandle = UseRef<OverlayHandle?>(null);
        var volHandle = UseRef<OverlayHandle?>(null);
        var stripRef = UseRef<NodeHandle>(default);
        var fadeArmed = UseRef(false);
        UseLayoutEffect(() =>
        {
            if (!fadeArmed.Value) { fadeArmed.Value = true; return; }   // born at its terminal
            props.FadeChrome(Context, stripRef.Value, show);
        }, show ? 1 : 0);

        void ToggleMore()
        {
            if (moreHandle.Value is { IsOpen: true } open) { open.Close(); return; }
            moreHandle.Value = overlay.Open(
                () => moreAnchor.Value,
                () => MenuFlyout.Create(props.MoreMenu(), () => moreHandle.Value?.Close(), size: OverlayFit.MenuSize),
                FlyoutPlacement.TopEdgeAlignedRight,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false, OpaqueSurface = true });
            if (moreHandle.Value is { } h) h.ClosedAction = () => moreHandle.Value = null;
        }

        void ToggleVolume()
        {
            if (volHandle.Value is { IsOpen: true } open) { open.Close(); return; }
            volHandle.Value = overlay.Open(
                () => volAnchor.Value,
                () => new BoxEl
                {
                    Width = ZunePlayerLayout.VolumePopupW, Height = ZunePlayerLayout.VolumePopupH, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Children =
                    [
                        Slider.Create(props.VolumeCell, props.SetVolume, new Slider.SliderOptions { Vertical = true, IsThumbToolTipEnabled = false },
                            length: ZunePlayerLayout.VolumePopupRail, thickness: ZunePlayerLayout.VolumeThickness, style: ZunePlayerBar.LineSlider()),
                    ],
                },
                FlyoutPlacement.TopCenter,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup) { ConstrainToRootBounds = false, OpaqueSurface = true });
            if (volHandle.Value is { } h) h.ClosedAction = () => volHandle.Value = null;
        }

        return new BoxEl
        {
            Grow = 1f, Direction = 1, Justify = FlexJustify.End, HitTestPassThrough = true,
            Children =
            [
                new BoxEl
                {
                    Shrink = 0f, Direction = 1, Padding = Edges4.All(O.Pad), Gradient = Tok.ScrimBottom,
                    Opacity = show ? 1f : 0f, HitTestVisible = show,
                    OnRealized = h => stripRef.Value = h,
                    OnPointerMoveWithin = _onPointerMoveWithin, OnPointerExit = _onPointerExit,
                    Children =
                    [
                        new BoxEl
                        {
                            Height = O.SeekRowH, Direction = 0, AlignItems = FlexAlign.Center, Gap = O.Gap, MinWidth = 0f,
                            Children =
                            [
                                props.TimeText(false, Tok.OnMediaSecondary, props.Visible),
                                new BoxEl { Grow = 1f, Shrink = 1f, MinWidth = 0f, Direction = 0, AlignItems = FlexAlign.Center, Children = [props.SeekBar(props.Feed, props.Visible)] },
                                props.TimeText(true, Tok.OnMediaSecondary, props.Visible),
                            ],
                        },
                        new BoxEl
                        {
                            Direction = 0, AlignItems = FlexAlign.Center, Gap = O.Gap, MinWidth = 0f,
                            Children =
                            [
                                ToolTip.Wrap(ZuneOnMedia.Glyph(Icons.Previous, props.Previous, facts.PrevEnabled), Loc.Get(Strings.Player.Previous)),
                                ZuneOnMedia.Disc(props.TogglePlay, facts.Playing, facts.CanPlay),
                                ToolTip.Wrap(ZuneOnMedia.Glyph(Icons.Next, props.Next, facts.NextEnabled), Loc.Get(Strings.Player.Next)),
                                new BoxEl { Grow = 1f, Shrink = 1f, MinWidth = 0f, HitTestVisible = false },
                                ToolTip.Wrap(ZuneOnMedia.Glyph(facts.Muted ? Icons.Mute : Icons.Volume, props.ToggleMute, true),
                                    Loc.Get(facts.Muted ? Strings.Player.Unmute : Strings.Player.Mute)),
                                ToolTip.Wrap(ZuneOnMedia.Glyph(Icons.ChevronUp, ToggleVolume, true, h => volAnchor.Value = h), Loc.Get(Strings.Player.Volume)),
                                ToolTip.Wrap(ZuneOnMedia.Glyph(Icons.More, ToggleMore, true, h => moreAnchor.Value = h), Loc.Get(Strings.Common.More)),
                            ],
                        },
                    ],
                },
            ],
        };
    }
}
