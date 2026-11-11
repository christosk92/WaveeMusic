// ── Themes/Classic/Structure/Views/Player/ClassicOnMedia.cs — Classic's `Player.OnMediaTransport` (themes plan 6.1, T6b) ────────
//
// From `Shell/OnMedia.UI.cs` (`OverlayChrome`): the shared on-media surface player (the docked picture, the floating mini player, the
// pop-out, the fullscreen layer: engine chrome is off). It speaks the stage bar's language (`ClassicStageVideoBar`, the owner 2026-11-11:
// "mini players controls ... not like the fullscreen player controls"): the stage's foot scrim, the seek rail across the foot of the
// picture, and under it one row on the stage's dark pills, every control a 40 target in the on-media ink:
//
//   [ play · previous · next · mute ] [ 2:18 / 3:50 ] ..................................... [ volume ^ · … ]
//
// The pills, the glyph buttons, the scrim and the time pill are the stage bar's own builders; the numbers are `ClassicOnMediaLayout`'s over
// `ClassicStageVideoBarGeometry`'s. The row folds as the picture narrows (`ClassicOnMediaLayout.Fold`): the time to the elapsed time, then
// the volume pair (mute, the slider's chevron) into "…", then previous and next into "…". Play and "…" never fold; a folded control is a
// row of "…" (the volume's opens the same slider card, at "…").
//
// WHY THIS IS NOT HOVER-REVEALED. The first cut used the card idiom (`Opacity = 0, HoverOpacity = 1`), which is right
// for a strip inside a hover container and wrong here: a reveal is driven by the nearest INTERACTIVE ancestor
// (`AnimScheduler.Hover.EnclosingScopeHovered`), and the pop-out window's tree has no pointer handler above the stage
// at all — so in the pop-out the controls never appeared, on any pointer input. They are driven instead by the video
// element's OWN idle machine (`MediaPlayerElement.ChromeVisibleOut`): one machine per surface, already carrying dwell,
// the leave debounce, the scrub / menu / keyboard / window-move holds and the accessibility override, and already the
// thing that decides when the cursor hides. Two timers could not agree; one cannot disagree.
//
// The bar is a component of the theme (it keeps the fade's armed flag and the captured nodes). Hovering a pill or the seek rail IS chrome
// activity: a pointer parked over ⋯/volume must not let the idle machine hide the very control it is sitting on. The scrim between the
// controls passes the pointer through to the picture (its click, the card's drag).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;

using Bar = Wavee.Themes.Classic.ClassicStageVideoBar;
using G = Wavee.Themes.Classic.ClassicStageVideoBarGeometry;
using M = Wavee.Themes.Classic.ClassicOnMediaLayout;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's on-media transport.</summary>
internal static class ClassicOnMedia
{
    /// <summary>The registered template of <see cref="ViewKeys.OnMediaTransport"/>.</summary>
    public static Element Template(OnMediaTransportProps props, in ViewContext ctx)
        => Embed.Comp(props, static () => new ClassicOverlayChrome());
}

/// <summary>The bar over a video hole. Revealed and concealed by the surface's idle machine (<c>Visible</c>), never hover (file
/// header).</summary>
internal sealed class ClassicOverlayChrome : Component
{
    Action<bool>? _overControls;

    public override Element Render()
    {
        var props = UseProps<OnMediaTransportProps>();
        _overControls ??= over => props.Feed.SetPointerOverControls(over);
        var overControls = _overControls;

        bool show = props.Visible.Value;
        var facts = props.Read();
        UseSignalEffect(() => props.VolumeCell.SetIfChanged(props.Volume.Value));
        // the picture's width (this bar fills it) decides the fold; the fold, not the width, re-renders the bar
        var width = UseMeasuredWidth("classic.onmedia");
        int fold = UseComputed(() => M.Fold(width.Value)).Value;
        var overlay = UseContext(Overlay.Service);
        var moreAnchor = UseRef<NodeHandle>(default);
        var volAnchor = UseRef<NodeHandle>(default);
        var moreHandle = UseRef<OverlayHandle?>(null);
        var volHandle = UseRef<OverlayHandle?>(null);
        var volumeAfterMore = UseRef(false);
        var stripRef = UseRef<NodeHandle>(default);
        var fadeArmed = UseRef(false);
        UseLayoutEffect(() =>
        {
            if (!fadeArmed.Value) { fadeArmed.Value = true; return; }   // born at its terminal
            props.FadeChrome(Context, stripRef.Value, show);
        }, show ? 1 : 0);

        bool volumeShown = fold < M.FoldVolume, skipShown = fold < M.FoldSkip;

        // "…": the controls the row folded away first (one row each, the bar's order), then the surface's own rows (OnMedia.MoreMenu)
        IReadOnlyList<MenuFlyoutItem> MoreItems()
        {
            var rows = props.MoreMenu();
            if (volumeShown) return rows;
            var items = new List<MenuFlyoutItem>(rows.Count + 5);
            if (!skipShown)
            {
                items.Add(new MenuFlyoutItem(Loc.Get(Strings.Player.Previous), Icons.Previous, facts.PrevEnabled, props.Previous));
                items.Add(new MenuFlyoutItem(Loc.Get(Strings.Player.Next), Icons.Next, facts.NextEnabled, props.Next));
            }
            items.Add(new MenuFlyoutItem(Loc.Get(facts.Muted ? Strings.Player.Unmute : Strings.Player.Mute), facts.Muted ? Icons.Volume : Icons.Mute, true, props.ToggleMute));
            // the slider card opens at "…" once the menu has closed (two popups never stand at once)
            items.Add(new MenuFlyoutItem(Loc.Get(Strings.Player.Volume), Icons.ChevronUp, true, () => volumeAfterMore.Value = true));
            items.Add(MenuFlyoutItem.Separator);
            for (int i = 0; i < rows.Count; i++) items.Add(rows[i]);
            return items;
        }

        void ToggleMore()
        {
            if (moreHandle.Value is { IsOpen: true } open) { open.Close(); return; }
            volumeAfterMore.Value = false;
            moreHandle.Value = overlay.Open(
                () => moreAnchor.Value,
                () => MenuFlyout.Create(MoreItems(), () => moreHandle.Value?.Close(), size: OverlayFit.MenuSize),
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
            if (moreHandle.Value is { } h)
                h.ClosedAction = () =>
                {
                    moreHandle.Value = null;
                    if (volumeAfterMore.Value) { volumeAfterMore.Value = false; ToggleVolume(moreAnchor); }
                };
        }

        void ToggleVolume(Ref<NodeHandle> anchor)
        {
            if (volHandle.Value is { IsOpen: true } open) { open.Close(); return; }
            volHandle.Value = overlay.Open(
                () => anchor.Value,
                () => new BoxEl
                {
                    Width = M.VolumePopupW, Height = M.VolumePopupH, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Padding = M.VolumePopupPadding,
                    Children =
                    [
                        VolumeSlider.Create(props.VolumeCell, props.SetVolume,
                            new Slider.SliderOptions { Vertical = true, IsThumbToolTipEnabled = false },
                            length: M.VolumeSliderLength, thickness: M.VolumeSliderThickness),
                    ],
                },
                FlyoutPlacement.TopCenter,
                // A slider card, not a menu: PopupChrome.Popup is the right chrome (WinUI FlyoutPresenter), which
                // means it stays in-app and over the video hole has nothing to blur. Declaring OpaqueSurface is what
                // says so: the engine never infers it from the hole's rect (a plate over UI painted on the hole keeps
                // its acrylic).
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup)
                    { ConstrainToRootBounds = false, OpaqueSurface = true });
            if (volHandle.Value is { } h) h.ClosedAction = () => volHandle.Value = null;
        }

        var ink = R.OnMedia.Ink;
        var left = new List<Element>(4)
        {
            Bar.Glyph(facts.Playing ? Icons.Pause : Icons.Play, props.TogglePlay, facts.CanPlay, G.PlayGlyph) with { Key = "onmedia:bar:play" },
        };
        if (skipShown)
        {
            left.Add(Slot("prev", ToolTip.Wrap(Bar.Glyph(Icons.Previous, props.Previous, facts.PrevEnabled), Loc.Get(Strings.Player.Previous))));
            left.Add(Slot("next", ToolTip.Wrap(Bar.Glyph(Icons.Next, props.Next, facts.NextEnabled), Loc.Get(Strings.Player.Next))));
        }
        // The glyph MUTES, like every other mute glyph; the chevron opens the slider card.
        if (volumeShown)
            left.Add(Slot("mute", ToolTip.Wrap(Bar.Glyph(facts.Muted ? Icons.Mute : Icons.Volume, props.ToggleMute, true), Loc.Get(facts.Muted ? Strings.Player.Unmute : Strings.Player.Mute))));

        var right = new List<Element>(2);
        if (volumeShown)
            right.Add(Slot("volume", ToolTip.Wrap(Bar.Glyph(Icons.ChevronUp, () => ToggleVolume(volAnchor), true, onRealized: h => volAnchor.Value = h), Loc.Get(Strings.Player.Volume))));
        right.Add(ToolTip.Wrap(Bar.Glyph(Icons.More, ToggleMore, true, onRealized: h => moreAnchor.Value = h) with { Key = "onmedia:bar:more" }, Loc.Get(Strings.Common.More)));

        var times = fold < M.FoldTime
            ? new[] { props.TimeText(false, ink, props.Visible), Bar.Folds(Bar.TimeSlash() with { Key = "onmedia:bar:time:slash" }), Bar.Folds(props.TimeText(true, ink, props.Visible) with { Key = "onmedia:bar:time:total" }) }
            : new[] { props.TimeText(false, ink, props.Visible) };

        return new BoxEl
        {
            Grow = 1f, Direction = 1, Justify = FlexJustify.End, HitTestPassThrough = true,
            Children =
            [
                new BoxEl
                {
                    // the stage bar's band: the scrim's fade rises over the picture above the seek row and the pill row WITHOUT taking layout room
                    // (the negative top margin): the surface sizes to its content, so a bar taller than the shortest picture (the docked cap's
                    // 120 floor) would stretch the presenter past the cap that clips it
                    Key = "onmedia:bar", Height = G.BarH + G.ScrimFade, Margin = new Edges4(0f, -G.ScrimFade, 0f, 0f), Shrink = 0f, Direction = 1, Justify = FlexJustify.End,
                    Padding = new Edges4(M.Inset, 0f, M.Inset, G.FootInset), HitTestPassThrough = true,
                    Gradient = Bar.ScrimGradient(Wavee.Theming.Roles.StageInk.Arm(Tok.Theme)),
                    // TERMINAL; FadeChrome above seeds the eased approach to it.
                    Opacity = show ? 1f : 0f,
                    // Concealed, a click belongs to the picture (play/pause), not to an invisible bar.
                    HitTestVisible = show,
                    OnRealized = h => stripRef.Value = h,
                    Children =
                    [
                        new BoxEl
                        {
                            Key = "onmedia:bar:seek", Height = G.SeekH, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, HitTestPassThrough = true,
                            Children =
                            [
                                new BoxEl
                                {
                                    Grow = 1f, Shrink = 1f, MinWidth = 0f, OnHoverMove = _ => overControls(true), OnPointerExit = () => overControls(false),
                                    Children = [props.SeekBar(props.Feed, props.Visible)],
                                },
                            ],
                        },
                        new BoxEl
                        {
                            Key = "onmedia:bar:row", Height = G.PillH, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, HitTestPassThrough = true,
                            Children =
                            [
                                Bar.Pill(left.ToArray(), overControls) with { Key = "onmedia:bar:transport", Layout = ClassicStageTheatre.Move },
                                Bar.Pill([Bar.TimeBox(times)], overControls) with { Key = "onmedia:bar:time", Margin = new Edges4(G.PillGap, 0f, 0f, 0f), Layout = ClassicStageTheatre.Move },
                                new BoxEl { Key = "onmedia:bar:spacer", Grow = 1f, Shrink = 1f, MinWidth = G.PillGap, HitTestVisible = false },
                                Bar.Pill(right.ToArray(), overControls) with { Key = "onmedia:bar:actions", Layout = ClassicStageTheatre.Move },
                            ],
                        },
                    ],
                },
            ],
        };
    }

    /// <summary><paramref name="control"/> keyed as its slot in the row and folding with it (the stage bar's swap).</summary>
    static Element Slot(string slot, Element control) => Bar.Folds(control with { Key = "onmedia:bar:slot:" + slot });
}
