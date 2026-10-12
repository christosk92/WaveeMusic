// ── Themes/Classic/Structure/Views/Player/ClassicOnMedia.cs — Classic's `Player.OnMediaTransport` (themes plan 6.1, T6b) ────────
//
// From `Shell/OnMedia.UI.cs` (`OverlayChrome`): the shared on-media surface player. Stage's transport language on every video
// overlay (engine chrome is off): previous · play · next, the seek rail, the times, mute, the volume popup and "⋯".
//
// WHY THIS IS NOT HOVER-REVEALED. The first cut used the card idiom (`Opacity = 0, HoverOpacity = 1`), which is right
// for a strip inside a hover container and wrong here: a reveal is driven by the nearest INTERACTIVE ancestor
// (`AnimScheduler.Hover.EnclosingScopeHovered`), and the pop-out window's tree has no pointer handler above the stage
// at all — so in the pop-out the controls never appeared, on any pointer input. They are driven instead by the video
// element's OWN idle machine (`MediaPlayerElement.ChromeVisibleOut`): one machine per surface, already carrying dwell,
// the leave debounce, the scrub / menu / keyboard / window-move holds and the accessibility override, and already the
// thing that decides when the cursor hides. Two timers could not agree; one cannot disagree.
//
// The strip is a component of the theme (it keeps the fade's armed flag and the captured nodes). Hovering the transport IS chrome
// activity: a pointer parked over ⋯/volume must not let the idle machine hide the very control it is sitting on.

using FluentGpu.Controls;
using FluentGpu.Controls.Media;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's on-media transport.</summary>
internal static class ClassicOnMedia
{
    /// <summary>The registered template of <see cref="ViewKeys.OnMediaTransport"/>.</summary>
    public static Element Template(OnMediaTransportProps props, in ViewContext ctx)
        => Embed.Comp(props, static () => new ClassicOverlayChrome());

    internal static Element Glyph(string glyph, Action onClick, bool enabled, Action<NodeHandle>? realized = null) => new BoxEl
    {
        Width = ClassicOnMediaLayout.GlyphBox, Height = ClassicOnMediaLayout.GlyphBox, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = Radii.ControlAll,
        Fill = ColorF.Transparent,
        HoverFill = enabled ? Tok.OnMediaPrimary with { A = ClassicOnMediaLayout.GlyphHoverAlpha } : ColorF.Transparent,
        PressedFill = enabled ? Tok.OnMediaPrimary with { A = ClassicOnMediaLayout.GlyphPressedAlpha } : ColorF.Transparent,
        BrushTransitionMs = Design.Motion.Faster,
        HoverScale = R.Motion.ScaleEmphatic.HoverIf(enabled),
        PressScale = R.Motion.ScaleEmphatic.PressIf(enabled),
        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false,
        IsEnabled = enabled, OnClick = enabled ? onClick : null, Cursor = enabled ? CursorId.Hand : (CursorId?)null,
        OnRealized = realized,
        Children =
        [
            new TextEl(glyph)
            {
                Size = ClassicOnMediaLayout.GlyphSize, FontFamily = Tok.Definition.Fonts.Icon,
                Color = enabled ? Tok.OnMediaSecondary : Tok.OnMediaTertiary,
                HoverColor = enabled ? Tok.OnMediaPrimary : Tok.OnMediaTertiary,
            },
        ],
    };

    internal static Element Play(Action togglePlay, bool playing, bool enabled) => new BoxEl
    {
        Width = ClassicOnMediaLayout.PlayBox, Height = ClassicOnMediaLayout.PlayBox, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = Radii.Circle(ClassicOnMediaLayout.PlayBox),
        Fill = enabled ? Tok.OnMediaPrimary with { A = ClassicOnMediaLayout.PlayAlpha } : ColorF.Transparent,
        HoverFill = enabled ? Tok.OnMediaPrimary with { A = ClassicOnMediaLayout.PlayHoverAlpha } : ColorF.Transparent,
        PressedFill = enabled ? Tok.OnMediaPrimary with { A = ClassicOnMediaLayout.PlayPressedAlpha } : ColorF.Transparent,
        BrushTransitionMs = Design.Motion.Faster,
        HoverScale = R.Motion.ScaleEmphatic.HoverIf(enabled),
        PressScale = R.Motion.ScaleEmphatic.PressIf(enabled),
        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false,
        IsEnabled = enabled, OnClick = enabled ? togglePlay : null,
        Cursor = enabled ? CursorId.Hand : (CursorId?)null,
        Children =
        [
            new TextEl(playing ? Icons.Pause : Icons.Play)
            {
                Size = ClassicOnMediaLayout.PlayGlyph, FontFamily = Tok.Definition.Fonts.Icon,
                Color = enabled ? Tok.OnMediaPrimary : Tok.OnMediaTertiary,
            },
        ],
    };
}

/// <summary>The strip over a video hole. Revealed and concealed by the surface's idle machine (<c>Visible</c>), never hover (file
/// header).</summary>
internal sealed class ClassicOverlayChrome : Component
{
    Action<Point2>? _onStripPointerMoveWithin;
    Action? _onStripPointerExit;

    public override Element Render()
    {
        var props = UseProps<OnMediaTransportProps>();
        _onStripPointerMoveWithin ??= _ => props.Feed.SetPointerOverControls(true);
        _onStripPointerExit ??= () => props.Feed.SetPointerOverControls(false);

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
                () => MenuFlyout.Create(props.MoreMenu(), () => moreHandle.Value?.Close()),
                FlyoutPlacement.TopEdgeAlignedRight,
                // A MENU, so the DEFAULT chrome (Flyout) - which is also the only chrome that carries an OS window
                // material. `Chrome: PopupChrome.Popup` mapped to PopupWindowMaterial.None, which fails the second
                // conjunct of OverlayHost's `wantWindowed` and pinned this popup IN-APP despite the
                // ConstrainToRootBounds=false right here asking it to escape. In-app over a VIDEO HOLE is the one
                // place acrylic cannot work - the hole is a DestOut erase and the video is a sibling DComp visual
                // below the UI swapchain, so the layer samples premultiplied zero - so OverlayHost fell back to
                // FlatAcrylicFill(), #FCFCFC at .85: the flat opaque slab. Windowed, the plate paints only the
                // presenter's residual tint over the window's real DWM backdrop, and the video blurs through.
                //
                // OpaqueSurface is inert while the lease holds (`overVideo` is gated on `!osBacked`); it is the
                // frame-ONE fallback if a lease is ever refused, so a refusal degrades to a solid plate instead of
                // a menu that is nothing but its 1px ring and its text. Same declaration the engine's own
                // transport makes for its quality/speed pickers over this same hole.
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss)
                    { ConstrainToRootBounds = false, OpaqueSurface = true });
            if (moreHandle.Value is { } h) h.ClosedAction = () => moreHandle.Value = null;
        }

        void ToggleVolume()
        {
            if (volHandle.Value is { IsOpen: true } open) { open.Close(); return; }
            volHandle.Value = overlay.Open(
                () => volAnchor.Value,
                () => new BoxEl
                {
                    Width = ClassicOnMediaLayout.VolumePopupW, Height = ClassicOnMediaLayout.VolumePopupH, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Padding = ClassicOnMediaLayout.VolumePopupPadding,
                    Children =
                    [
                        Slider.Create(props.VolumeCell, props.SetVolume,
                            new Slider.SliderOptions { Vertical = true, IsThumbToolTipEnabled = false },
                            length: ClassicOnMediaLayout.VolumeSliderLength, thickness: ClassicOnMediaLayout.VolumeSliderThickness),
                    ],
                },
                FlyoutPlacement.TopCenter,
                // A slider card, not a menu: PopupChrome.Popup is the right chrome (WinUI FlyoutPresenter), which
                // means it stays in-app and over the video hole has nothing to blur. Declaring OpaqueSurface says
                // so from frame one instead of letting the geometric hole query discover it a frame later and flip
                // the fill under the user.
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup)
                    { ConstrainToRootBounds = false, OpaqueSurface = true });
            if (volHandle.Value is { } h) h.ClosedAction = () => volHandle.Value = null;
        }

        return new BoxEl
        {
            Grow = 1f, Direction = 1, Justify = FlexJustify.End, HitTestPassThrough = true,
            Children =
            [
                new BoxEl
                {
                    Shrink = 0f, Direction = 1, Gap = Spacing.XS,
                    Padding = new Edges4(Spacing.S, Spacing.S, Spacing.S, Spacing.S),
                    Gradient = Tok.ScrimBottom,
                    // TERMINAL; FadeChrome above seeds the eased approach to it.
                    Opacity = show ? 1f : 0f,
                    // Concealed, a click belongs to the picture (play/pause), not to an invisible strip.
                    HitTestVisible = show,
                    OnRealized = h => stripRef.Value = h,
                    // Hovering the transport IS chrome activity: a pointer parked over ⋯/volume must not let the
                    // idle machine hide the very control it is sitting on.
                    OnPointerMoveWithin = _onStripPointerMoveWithin,
                    OnPointerExit = _onStripPointerExit,
                    Children =
                    [
                        new BoxEl
                        {
                            Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Gap = Spacing.S,
                            Children =
                            [
                                ToolTip.Wrap(ClassicOnMedia.Glyph(Icons.Previous, props.Previous, facts.PrevEnabled), Loc.Get(Strings.Player.Previous)),
                                ClassicOnMedia.Play(props.TogglePlay, facts.Playing, facts.CanPlay),
                                ToolTip.Wrap(ClassicOnMedia.Glyph(Icons.Next, props.Next, facts.NextEnabled), Loc.Get(Strings.Player.Next)),
                            ],
                        },
                        props.SeekBar(props.Feed, props.Visible),
                        new BoxEl
                        {
                            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinWidth = 0f,
                            Children =
                            [
                                props.TimeText(false, Tok.OnMediaSecondary, props.Visible),
                                new BoxEl { Grow = 1f, Shrink = 1f, MinWidth = 0f, HitTestVisible = false },
                                // The glyph MUTES, like every other mute glyph; the slider keeps its own affordance beside it.
                                ToolTip.Wrap(ClassicOnMedia.Glyph(facts.Muted ? Icons.Mute : Icons.Volume, props.ToggleMute, true),
                                    Loc.Get(facts.Muted ? Strings.Player.Unmute : Strings.Player.Mute)),
                                ToolTip.Wrap(ClassicOnMedia.Glyph(Icons.ChevronUp, ToggleVolume, true, h => volAnchor.Value = h),
                                    Loc.Get(Strings.Player.Volume)),
                                ToolTip.Wrap(ClassicOnMedia.Glyph(Icons.More, ToggleMore, true, h => moreAnchor.Value = h),
                                    Loc.Get(Strings.Common.More)),
                                props.TimeText(true, Tok.OnMediaSecondary, props.Visible),
                            ],
                        },
                    ],
                },
            ],
        };
    }
}
