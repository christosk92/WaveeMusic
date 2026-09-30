// ── Shell/OnMedia.UI.cs ────────────────────────────────────────────────────────────────────────────────────────────
// the shared on-media surface player: Stage's transport language on every video overlay (engine chrome is off)
//
// Role: UI
// Owner: K
//
// WHY THIS IS NOT HOVER-REVEALED. The first cut used the card idiom (`Opacity = 0, HoverOpacity = 1`), which is right
// for a strip inside a hover container and wrong here: a reveal is driven by the nearest INTERACTIVE ancestor
// (`AnimScheduler.Hover.EnclosingScopeHovered`), and the pop-out window's tree has no pointer handler above the stage
// at all — so in the pop-out the controls never appeared, on any pointer input. They are driven instead by the video
// element's OWN idle machine (`MediaPlayerElement.ChromeVisibleOut`): one machine per surface, already carrying dwell,
// the leave debounce, the scrub / menu / keyboard / window-move holds and the accessibility override, and already the
// thing that decides when the cursor hides. Two timers could not agree; one cannot disagree.

using System.Collections.Generic;

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Controls.Media;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;

namespace Wavee;

public static class OnMedia
{
    public const string TransportId = Video.OnMediaTransport.Id;

    static readonly Action s_prev = static () => Playback.Previous();
    static readonly Action s_next = static () => Playback.Next();
    static readonly float[] s_rates = [0.5f, 0.75f, 1f, 1.25f, 1.5f, 1.75f, 2f];
    static readonly FloatSignal s_volume = new(1f);

    /// <summary>The on-media transport over a video hole. Ink, seek bar and time labels are Stage's / the player bar's.
    /// Revealed and concealed by <paramref name="visible"/> — the surface's idle machine, never hover (file header).
    /// <paramref name="feed"/> is the SAME window's <c>PlayerChromeFeed</c> the video element reports activity to, so
    /// the pointer sitting over a transport button counts as chrome activity too (a pointer parked on ⋯ must not let
    /// the idle machine hide the very menu button it is over).</summary>
    public static Element Transport(IReadSignal<bool> visible, PlayerChromeFeed feed) => Embed.Comp(() => new OverlayChrome { Visible = visible, Feed = feed });

    /// <summary>The on-media chrome fade, the engine transport's own idiom: asymmetric by token (reveal 150 ms
    /// decelerate, conceal 400 ms ease-out), reduced motion resolved as a VALUE by <c>EffectiveDurationMs</c> and never
    /// as a branch here. The caller's static <c>Opacity</c> is the TERMINAL, so an unrelated re-render always
    /// re-asserts the right end value; this seeds the eased approach to it, FROM where the pixels actually are (a live
    /// track when a reveal interrupts a conceal, the previous terminal otherwise). Call it from a
    /// <c>UseLayoutEffect</c> keyed on <paramref name="show"/>, and skip the mount run: chrome is born at its
    /// terminal.</summary>
    public static void FadeChrome(RenderContext ctx, NodeHandle node, bool show)
    {
        var scene = ctx.Scene;
        if (ctx.Anim is not { } anim || scene is null || node.IsNull || !scene.IsLive(node)) return;
        var tok = show ? MotionTok.MediaChromeReveal : MotionTok.MediaChromeConceal;
        float to = show ? 1f : 0f;
        float ms = tok.EffectiveDurationMs(AnimChannel.Opacity);
        if (ms <= 0f) return;                       // reduced motion: the terminal already IS the answer
        float from = anim.TryGetTrackValue(node, AnimChannel.Opacity, out float live) ? live : 1f - to;
        if (MathF.Abs(from - to) < 0.001f) return;
        anim.SeedEased(node, AnimChannel.Opacity, from, to, ms, tok.Easing);
    }

    /// <summary>⋯ rows: WHERE the video plays at the top level, then the three settings as cascades — the same shape
    /// the engine's own transport uses for this menu (<c>MediaPlayerElement</c>: an Aspect ratio / Playback speed /
    /// Quality trio of sub-menus). Flat, the four groups ran to ~25 rows and scrolled INSIDE the presenter at its
    /// 468 DIP cap, so the rungs at the bottom were simply not visible.
    ///
    /// <para><see cref="Video.PlacementMenu"/> bundles the placement radios AND its own trailing extras (Always on
    /// top while detached, Turn off video while active), each behind its own separator. Those extras must stay LAST
    /// - "Turn off video" under three settings cascades reads as part of them - so the list is split at its first
    /// separator here rather than adding an overload to the shared builder: the player bar's chevron and its
    /// overflow cascade use the very same list and must not change shape.</para></summary>
    public static IReadOnlyList<MenuFlyoutItem> MoreMenu()
    {
        var placement = Video.PlacementMenu(includeFullscreen: true);
        int cut = 0;
        while (cut < placement.Count && !placement[cut].IsSeparator) cut++;

        var items = new List<MenuFlyoutItem>(12);
        for (int i = 0; i < cut; i++) items.Add(placement[i]);
        items.Add(MenuFlyoutItem.Separator);
        // Aspect carries the icon and the other two do not, matching the engine's transport exactly. Every cascade
        // here is non-empty by construction (four aspect rows plus a separator and four numeric-ratio rows, Auto plus
        // any rungs, the seven fixed rates), which is
        // the one real foot-gun: `OpenSub` accepts a non-null EMPTY list and opens a blank popup.
        items.Add(MenuFlyoutItem.SubMenu(Loc.Get(Strings.Player.AspectMenu), AspectRows(), Icons.Movie));
        items.Add(MenuFlyoutItem.SubMenu(Loc.Get(Strings.Player.QualityMenu), QualityRows()));
        items.Add(MenuFlyoutItem.SubMenu(Loc.Get(Strings.Player.SpeedMenu), SpeedRows()));
        for (int i = cut; i < placement.Count; i++) items.Add(placement[i]);
        return items;
    }

    /// <summary>The four modes, then the numeric ratios the engine's own (suppressed) transport offers — the only rows
    /// that can show a visibly different frame for Wavee's overwhelmingly 16:9 surfaces (docked, pop-out and mini are
    /// all within 3 DIP of 16:9, so Fit/Crop/Stretch barely differ there). <see cref="Video.AspectRatioRows"/> is the
    /// pure "which row is checked" rule; the ratio itself is captured per row by the lambda below, built fresh every
    /// time the menu opens.</summary>
    static IReadOnlyList<MenuFlyoutItem> AspectRows()
    {
        var mode = Video.Prefs.Aspect(Platform.Settings);
        double ratio = Video.Prefs.CustomRatio(Platform.Settings);
        return
        [
            MenuFlyoutItem.RadioItem(Loc.Get(Strings.Player.AspectFit), mode == Video.AspectPreference.Fit,
                static () => Video.Prefs.SetAspect(Platform.Settings, Video.AspectPreference.Fit, 0)),
            MenuFlyoutItem.RadioItem(Loc.Get(Strings.Player.AspectCrop), mode == Video.AspectPreference.Crop,
                static () => Video.Prefs.SetAspect(Platform.Settings, Video.AspectPreference.Crop, 0)),
            MenuFlyoutItem.RadioItem(Loc.Get(Strings.Player.AspectStretch), mode == Video.AspectPreference.Stretch,
                static () => Video.Prefs.SetAspect(Platform.Settings, Video.AspectPreference.Stretch, 0)),
            MenuFlyoutItem.RadioItem(Loc.Get(Strings.Player.AspectNative), mode == Video.AspectPreference.Native,
                static () => Video.Prefs.SetAspect(Platform.Settings, Video.AspectPreference.Native, 0)),
            MenuFlyoutItem.Separator,
            RatioRow(Strings.Player.AspectRatio169, Video.AspectRatioRows.Standard[0], mode, ratio),
            RatioRow(Strings.Player.AspectRatio43, Video.AspectRatioRows.Standard[1], mode, ratio),
            RatioRow(Strings.Player.AspectRatio219, Video.AspectRatioRows.Standard[2], mode, ratio),
            RatioRow(Strings.Player.AspectRatio239, Video.AspectRatioRows.Standard[3], mode, ratio),
        ];
    }

    static MenuFlyoutItem RatioRow(string key, double r, Video.AspectPreference mode, double current)
        => MenuFlyoutItem.RadioItem(Loc.Get(key), Video.AspectRatioRows.IsChecked(mode, current, r),
            () => Video.Prefs.SetAspect(Platform.Settings, Video.AspectPreference.Custom, r));

    /// <summary>Auto plus the rungs THIS manifest actually offers. A fixed 720/1080/1440/2160 ladder lied twice: a rung
    /// no variant matches does not pin (<c>Playback.Video.SetPreferredHeight</c> finds nothing to select) yet still
    /// moves the ABR ceiling, so the row read as selected while the player was really on Auto — and the rungs the
    /// manifest DID offer were missing from the menu.</summary>
    static IReadOnlyList<MenuFlyoutItem> QualityRows()
    {
        int pin = Platform.Settings.Get(Platform.Keys.VideoQuality);
        int[] rungs = Playback.Video.QualityRungs();
        var rows = new List<MenuFlyoutItem>(rungs.Length + 1)
        {
            MenuFlyoutItem.RadioItem(Loc.Get(Strings.Player.QualityAuto), pin == 0,
                static () => Playback.Video.SetPreferredHeight(0)),
        };
        for (int i = 0; i < rungs.Length; i++)
        {
            int h = rungs[i];
            rows.Add(MenuFlyoutItem.RadioItem(h.ToString(System.Globalization.CultureInfo.InvariantCulture) + "p", pin == h,
                () => Playback.Video.SetPreferredHeight(h)));
        }
        return rows;
    }

    static IReadOnlyList<MenuFlyoutItem> SpeedRows()
    {
        float now = Playback.EpisodeSpeed.Peek();
        var rows = new List<MenuFlyoutItem>(s_rates.Length);
        for (int i = 0; i < s_rates.Length; i++)
        {
            float rate = s_rates[i];
            string label = rate.ToString("0.##") + "×";
            rows.Add(MenuFlyoutItem.RadioItem(label, MathF.Abs(now - rate) < 0.01f, () => Playback.SetEpisodeSpeed(rate)));
        }
        return rows;
    }

    sealed class OverlayChrome : Component
    {
        public required IReadSignal<bool> Visible { get; init; }
        public required PlayerChromeFeed Feed { get; init; }

        readonly Action<Point2> _onStripPointerMoveWithin;
        readonly Action _onStripPointerExit;

        public OverlayChrome()
        {
            // `Feed` is a required init prop: it is set before the first Render and these delegates only run from the
            // mounted strip's handlers, so the read is never null despite what flow analysis can see in a ctor.
            _onStripPointerMoveWithin = _ => Feed!.SetPointerOverControls(true);
            _onStripPointerExit = () => Feed!.SetPointerOverControls(false);
        }

        public override Element Render()
        {
            bool show = Visible.Value;
            bool playing = Playback.IsPlaying.Value;
            bool can = !Playback.Current.Value.IsNone;
            var tf = Shell.TransportFacts();                           // the same skip fact set the bar and the stage read
            bool muted = Playback.Muted.Value;
            UseSignalEffect(static () => s_volume.SetIfChanged(Playback.Volume.Value));
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
                FadeChrome(Context, stripRef.Value, show);
            }, show ? 1 : 0);

            void ToggleMore()
            {
                if (moreHandle.Value is { IsOpen: true } open) { open.Close(); return; }
                moreHandle.Value = overlay.Open(
                    () => moreAnchor.Value,
                    () => MenuFlyout.Create(MoreMenu(), () => moreHandle.Value?.Close()),
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
                    static () => new BoxEl
                    {
                        Width = 52f, Height = 168f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                        Padding = new Edges4(10f, 14f, 10f, 14f),
                        Children =
                        [
                            Slider.Create(s_volume, static v => Playback.SetVolume(v),
                                new Slider.SliderOptions { Vertical = true, IsThumbToolTipEnabled = false },
                                length: 124f, thickness: 32f),
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
                                    ToolTip.Wrap(Glyph(Icons.Previous, s_prev, tf.PrevEnabled), Loc.Get(Strings.Player.Previous)),
                                    Play(playing, can),
                                    ToolTip.Wrap(Glyph(Icons.Next, s_next, tf.NextEnabled), Loc.Get(Strings.Player.Next)),
                                ],
                            },
                            Shell.SeekBar(Feed),
                            new BoxEl
                            {
                                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinWidth = 0f,
                                Children =
                                [
                                    Shell.TimeText(remaining: false, ink: Tok.OnMediaSecondary),
                                    new BoxEl { Grow = 1f, Shrink = 1f, MinWidth = 0f, HitTestVisible = false },
                                    // The glyph MUTES, like every other mute glyph; the slider keeps its own affordance beside it.
                                    ToolTip.Wrap(Glyph(muted ? Icons.Mute : Icons.Volume, static () => Playback.ToggleMute(), true),
                                        Loc.Get(muted ? Strings.Player.Unmute : Strings.Player.Mute)),
                                    ToolTip.Wrap(Glyph(Icons.ChevronUp, ToggleVolume, true, h => volAnchor.Value = h),
                                        Loc.Get(Strings.Player.Volume)),
                                    ToolTip.Wrap(Glyph(Icons.More, ToggleMore, true, h => moreAnchor.Value = h),
                                        Loc.Get(Strings.Common.More)),
                                    Shell.TimeText(remaining: true, ink: Tok.OnMediaSecondary),
                                ],
                            },
                        ],
                    },
                ],
            };
        }

        static Element Glyph(string glyph, Action onClick, bool enabled, Action<NodeHandle>? realized = null) => new BoxEl
        {
            Width = 36f, Height = 36f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = Radii.ControlAll,
            Fill = ColorF.Transparent,
            HoverFill = enabled ? Tok.OnMediaPrimary with { A = 0.14f } : ColorF.Transparent,
            PressedFill = enabled ? Tok.OnMediaPrimary with { A = 0.22f } : ColorF.Transparent,
            BrushTransitionMs = Design.Motion.Faster,
            HoverScale = Design.Motion.ScaleEmphatic.HoverIf(enabled),
            PressScale = Design.Motion.ScaleEmphatic.PressIf(enabled),
            Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false,
            IsEnabled = enabled, OnClick = enabled ? onClick : null, Cursor = enabled ? CursorId.Hand : (CursorId?)null,
            OnRealized = realized,
            Children =
            [
                new TextEl(glyph)
                {
                    Size = 16f, FontFamily = Theme.IconFont,
                    Color = enabled ? Tok.OnMediaSecondary : Tok.OnMediaTertiary,
                    HoverColor = enabled ? Tok.OnMediaPrimary : Tok.OnMediaTertiary,
                },
            ],
        };

        static Element Play(bool playing, bool enabled) => new BoxEl
        {
            Width = 44f, Height = 44f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = Radii.Circle(44f),
            Fill = enabled ? Tok.OnMediaPrimary with { A = 0.18f } : ColorF.Transparent,
            HoverFill = enabled ? Tok.OnMediaPrimary with { A = 0.28f } : ColorF.Transparent,
            PressedFill = enabled ? Tok.OnMediaPrimary with { A = 0.36f } : ColorF.Transparent,
            BrushTransitionMs = Design.Motion.Faster,
            HoverScale = Design.Motion.ScaleEmphatic.HoverIf(enabled),
            PressScale = Design.Motion.ScaleEmphatic.PressIf(enabled),
            Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false,
            IsEnabled = enabled, OnClick = enabled ? static () => Shell.TogglePlayPause("onmedia.button") : null,
            Cursor = enabled ? CursorId.Hand : (CursorId?)null,
            Children =
            [
                new TextEl(playing ? Icons.Pause : Icons.Play)
                {
                    Size = 18f, FontFamily = Theme.IconFont,
                    Color = enabled ? Tok.OnMediaPrimary : Tok.OnMediaTertiary,
                },
            ],
        };
    }
}
