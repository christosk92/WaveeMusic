// ── Themes/Classic/Structure/Views/Stage/ClassicStageVideoBar.cs — Classic's control bar over the picture (Video mode) ─────────────────
//
// YouTube's player bar, as the owner asked for it: in Video mode the transport belongs to the PICTURE. At the foot of the picture's rect (the whole
// stage in Cinema, the 16:9 box in Theatre; it moves with the reservation on `ClassicStageTheatre.Move`, as the ground does):
//
//   a scrim (the on-media scrim, so the controls read over bright video)
//   the seek bar across the picture at the stage's gutter
//   one row:  [ play · previous · next · mute (+ the slider it reveals on hover or focus) ] [ 0:05 / 3:04 ]
//             ............................................................  [ heart · shuffle · repeat · device · "…" · settings · Cinema/Theatre · Exit ]
//
// Each group stands on a dark translucent pill; every control is a 40 target. Shuffle and repeat show their latched state and the heart its saved
// state at rest. "…" is the now-playing menu (track actions, `Stage.NowPlayingMenu`); the settings gear is the docked video's own menu
// (`StageTransportProps.VideoMenu`: where it plays, aspect ratio, quality, speed). Both hold the idle chrome while open (`MenuOpen`). The row folds as
// the picture narrows (`ClassicStageVideoBarGeometry.Fold`): the slider, then shuffle · repeat · device, then the mute, then the time.
//
// The ink is the ON-MEDIA ladder (`R.OnMedia`): the bar stands over the picture, which is media in both arms (the docked video's strip reads the same).
// The bar is mounted by the picture chrome (`ClassicStagePictureChrome`) under the idle chrome's Show, in the video layer after the presenter.

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

using G = Wavee.Themes.Classic.ClassicStageVideoBarGeometry;
using Ink = Wavee.Theming.Roles.StageInk;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's control bar over the picture: its place (the picture's foot, moving with the reservation), the scrim and the seek row.
/// Its own component, so a Cinema ⇄ Theatre change or a resize re-renders the bar alone.</summary>
internal sealed class ClassicStageVideoBar : Component
{
    public sealed record Props(StageRootView View);

    static readonly FormatCache<int> s_percent = FormatCache.Create<int>();

    public override Element Render()
    {
        var v = UseProps<Props>().View;
        var st = v.Stage;
        var L = st.Layout.Value;
        var r = ClassicStageTheatre.PictureRect(L, v.ShowTheatre());
        float h = G.BarH + G.ScrimFade;
        float inset = L.TransportInset;
        int fold = G.Fold(r.W, inset, L.TheatreFits);
        var overControls = st.OverControls;
        var t = v.Transport;
        return Stage.Layer with
        {
            Children =
            [
                new BoxEl
                {
                    Key = "picture:bar", AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, ZStack = true, ClipToBounds = true, HitTestPassThrough = true,
                    Margin = new Edges4(r.X, r.Y + r.H - h, 0f, 0f), Width = r.W, Height = h, Layout = ClassicStageTheatre.Move,
                    Children =
                    [
                        new BoxEl
                        {
                            AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, HitTestVisible = false,
                            Gradient = GradientDown(new GradientStop(0f, Scrim(0f)), new GradientStop(G.ScrimSolidAt, Scrim(G.ScrimA)), new GradientStop(1f, Scrim(G.ScrimA))),
                        },
                        new BoxEl
                        {
                            AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.Stretch, Direction = 1, HitTestPassThrough = true,
                            Padding = new Edges4(inset, 0f, inset, G.FootInset),
                            Children =
                            [
                                new BoxEl
                                {
                                    Key = "picture:bar:seek", Height = G.SeekH, Direction = 0, AlignItems = FlexAlign.Center, HitTestPassThrough = true,
                                    Layout = ClassicStageTheatre.Move,   // a tier's gutter changing moves it, never snaps it
                                    Children =
                                    [
                                        new BoxEl
                                        {
                                            Grow = 1f, Shrink = 1f, MinWidth = 0f, OnHoverMove = _ => overControls(true), OnPointerExit = () => overControls(false),
                                            Children = [(t.SeekBarWithChrome ?? t.SeekBar)(st.Scrubbing)],   // holds while the bar idles out
                                        },
                                    ],
                                },
                                Embed.Comp(new RowProps(t, fold, L.TheatreFits), static () => new Row()),
                            ],
                        },
                    ],
                },
            ],
        };
    }

    /// <summary>The stage veil at alpha <paramref name="a"/> (the arm's: dark on the dark arm, light on the light, as Cinema's own scrims): the
    /// pills carry the controls' contrast over any picture.</summary>
    static ColorF Scrim(float a) => Ink.Veil with { A = a };

    sealed record RowProps(StageTransportProps T, int Fold, bool TheatreOffered);

    /// <summary>The row: the playback facts are read here (a play, a like, a shuffle re-render the row and nothing above it).</summary>
    sealed class Row : Component
    {
        public override Element Render()
        {
            var p = UseProps<RowProps>();
            var t = p.T;
            var st = t.Stage;
            var f = t.Facts();
            var verbs = t.Verbs;
            var set = st.Accent.Value;
            // the state colour ON the dark pills: the stage's accent where the arm is dark (it is lifted for a dark ground); on the light arm the
            // accent is solved as ink for a pale ground and reads as nothing on a dark pill, so the on-media ink marks the state there
            var lit = Ink.IsDark ? set.Fill : R.OnMedia.Ink;
            var overlay = UseContext(Overlay.Service);
            var menuOpen = st.MenuOpen;
            var overControls = st.OverControls;
            int fold = p.Fold;
            var ink = R.OnMedia.Ink;

            var left = new List<Element>(4)
            {
                Glyph(f.Playing ? Icons.Pause : Icons.Play, () => verbs.TogglePlay("stage.button"), f.PrimaryEnabled, G.PlayGlyph) with { Key = "picture:bar:play" },
                K("prev", ToolTip.Wrap(Glyph(Icons.Previous, verbs.Previous, f.PrevEnabled), Loc.Get(Strings.Player.Previous))),
                K("next", ToolTip.Wrap(Glyph(Icons.Next, verbs.Next, f.NextEnabled), Loc.Get(Strings.Player.Next))),
            };
            if (fold < G.FoldMute) left.Add(Fold("volume", Embed.Comp(new VolumeProps(t, fold < G.FoldSlider), static () => new Volume())));

            var right = new List<Element>(8) { K("heart", Heart(f.Saved, f.Like, lit)) };
            if (fold < G.FoldSatellites)
            {
                right.Add(Fold("shuffle", ToolTip.Wrap(Toggle(Icons.Shuffle, verbs.ToggleShuffle, f.CanTransport, f.Shuffle, lit), Loc.Get(Strings.Player.Shuffle))));
                right.Add(Fold("repeat", ToolTip.Wrap(Toggle(f.RepeatOne ? Icons.RepeatOne : Icons.RepeatAll, verbs.CycleRepeat, f.CanTransport, f.RepeatOn, lit), Loc.Get(Strings.Player.Repeat))));
                right.Add(Fold("device", Embed.Comp(new ClassicStageTransport.DeviceButton.Props(t, OnPicture: true), static () => new ClassicStageTransport.DeviceButton())));
            }
            right.Add(K("more", ToolTip.Wrap((Glyph(Icons.More, null, true) with { Key = "picture:bar:more", ClickRequestsContext = true })
                .WithContextMenu(overlay,
                    () => { var m = Stage.NowPlayingMenu?.Invoke(); if (m is { } model && ContextMenu.HasAnyEnabled(model)) menuOpen(true); return m; },   // the "…" menu holds the chrome (V-U22); an empty menu never opens
                    new ContextMenuOptions { OnClosed = _ => menuOpen(false) }), Loc.Get(Strings.Common.More))));
            if (t.VideoMenu is not null) right.Add(K("settings", Embed.Comp(new MenuProps(t), static () => new VideoMenuButton())));
            if (p.TheatreOffered)
            {
                bool theatre = st.Theatre.Value;
                right.Add(Fold("theatre", ToolTip.Wrap(Glyph(theatre ? Icons.FullScreen : Icons.SplitView, st.ToggleTheatre, true) with { Key = "stage:bar:theatre" },
                    Loc.Get(theatre ? Strings.Stage.FillScreen : Strings.Stage.TheatreMode))));
            }
            right.Add(K("exit", ToolTip.Wrap(Glyph(Icons.BackToWindow, st.Exit, true) with { Key = "stage:bar:exit" }, Loc.Get(Strings.Stage.Exit))));

            var row = new List<Element>(4) { Pill(left.ToArray(), overControls) with { Key = "picture:bar:transport" } };
            if (fold < G.FoldTime)
                row.Add(Pill(
                [
                    new BoxEl
                    {
                        Height = G.Button, Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.XS, Padding = new Edges4(G.TimePadX, 0f, G.TimePadX, 0f),
                        Children = [(t.TimeTextWithChrome ?? t.TimeText)(false, ink), Caption("/") with { Color = R.OnMedia.Ink }, (t.TimeTextWithChrome ?? t.TimeText)(true, ink)],   // loc-allow: the times' own separator
                    },
                ], overControls) with
                {
                    Key = "picture:bar:time", Margin = new Edges4(G.PillGap, 0f, 0f, 0f),
                    Enter = ClassicTierMotion.SwapFade, Exit = ClassicTierMotion.SwapFade, Transition = ClassicTierMotion.SwapFadeMotion,   // a folded slot
                });
            row.Add(new BoxEl { Key = "picture:bar:spacer", Grow = 1f, MinWidth = G.PillGap, HitTestVisible = false });
            row.Add(Pill(right.ToArray(), overControls) with { Key = "picture:bar:actions" });
            return new BoxEl
            {
                Key = "picture:bar:row", Height = G.PillH, Direction = 0, AlignItems = FlexAlign.Center, HitTestPassThrough = true,
                Layout = ClassicStageTheatre.Move, Children = row.ToArray(),   // a tier's gutter or a fold moves the row, never snaps it
            };
        }
    }

    /// <summary><paramref name="control"/> keyed as its slot in the row: a fold that adds or drops a control never shifts the ones after it into
    /// another slot (which rebuilt them).</summary>
    static Element K(string slot, Element control) => control with { Key = "picture:bar:slot:" + slot };

    /// <summary>A slot the row FOLDS (<see cref="ClassicStageVideoBarGeometry.Fold"/>): it comes and goes as the picture widens or narrows (a tier
    /// crossing changes the Theatre panel's width), cross-fading as the theme's declared swap (<see cref="ClassicTierMotion.SwapFade"/>).</summary>
    static Element Fold(string slot, Element control) => K(slot, control) with
    {
        Enter = ClassicTierMotion.SwapFade, Exit = ClassicTierMotion.SwapFade, Transition = ClassicTierMotion.SwapFadeMotion,
    };

    /// <summary>A group of controls on its dark pill; the pointer over it holds the chrome (the controls' hold, never the empty row's).</summary>
    static BoxEl Pill(Element[] kids, Action<bool> overControls) => new()
    {
        Height = G.PillH, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Padding = Edges4.All(G.PillPad), Corners = Radii.Circle(G.PillH),
        Fill = R.OnMedia.CoverScrim with { A = G.PillA },
        OnHoverMove = _ => overControls(true), OnPointerExit = () => overControls(false),
        Children = kids,
    };

    /// <summary>A 40 glyph button in the on-media ink (a plate on hover and press).</summary>
    internal static BoxEl Glyph(string glyph, Action? onClick, bool enabled, float size = G.Glyph, Action<NodeHandle>? onRealized = null) => new()
    {
        Width = G.Button, Height = G.Button, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = Radii.Circle(G.Button),
        Fill = ColorF.Transparent,
        HoverFill = enabled ? R.OnMedia.Ink with { A = G.GlyphHoverA } : ColorF.Transparent,
        PressedFill = enabled ? R.OnMedia.Ink with { A = G.GlyphPressedA } : ColorF.Transparent,
        BrushTransitionMs = Design.Motion.Faster,
        HoverScale = R.Motion.ScaleEmphatic.HoverIf(enabled), PressScale = R.Motion.ScaleEmphatic.PressIf(enabled),
        Role = AutomationRole.Button, Focusable = enabled, AllowFocusOnInteraction = false, IsEnabled = enabled, OnClick = enabled ? onClick : null,
        Cursor = enabled ? CursorId.Hand : (CursorId?)null, OnRealized = onRealized,
        Children = [new TextEl(glyph) { Size = size, FontFamily = Tok.Definition.Fonts.Icon, Color = enabled ? R.OnMedia.Ink : R.OnMedia.InkTertiary }],
    };

    /// <summary>Shuffle / repeat: the accent plate while latched (visible at rest).</summary>
    static BoxEl Toggle(string glyph, Action onClick, bool enabled, bool latched, ColorF accent)
    {
        bool lit = latched && enabled;
        return Glyph(glyph, onClick, enabled) with
        {
            Fill = lit ? accent : ColorF.Transparent,
            HoverFill = lit ? accent with { A = G.LatchedHoverA } : enabled ? R.OnMedia.Ink with { A = G.GlyphHoverA } : ColorF.Transparent,
            Children = [new TextEl(glyph) { Size = G.Glyph, FontFamily = Tok.Definition.Fonts.Icon, Color = !enabled ? R.OnMedia.InkTertiary : lit ? ColorContrast.PickContrast(accent) : R.OnMedia.Ink }],
        };
    }

    /// <summary>The heart: filled in the accent while the track is saved (visible at rest).</summary>
    static BoxEl Heart(bool saved, Action? onLike, ColorF accent) => Glyph(saved ? Icons.HeartFill : Icons.Heart, onLike, onLike is not null, G.HeartGlyph) with
    {
        BlocksDragArm = true,
        Children = [new BoxEl { Key = saved ? "sh:on" : "sh:off", Children = [new TextEl(saved ? Icons.HeartFill : Icons.Heart) { Size = G.HeartGlyph, FontFamily = Tok.Definition.Fonts.Icon, Color = saved ? accent : R.OnMedia.Ink }] }],
    };

    sealed record VolumeProps(StageTransportProps T, bool Slider);

    /// <summary>The mute button and, where the row has room for it, the volume slider it reveals while the pointer is over the group or the mute has
    /// keyboard focus (YouTube's): the slider's width eases open and shut, pushing the time along. Its own component: a mute, a volume drag or the
    /// reveal re-render the group alone.</summary>
    sealed class Volume : Component
    {
        static readonly LayoutTransition s_reveal = new(TransitionChannels.Bounds, TransitionDynamics.Tween(G.VolumeRevealMs, Easing.FluentDecelerate), SizeMode.Relayout);
        static readonly Slider.SliderOptions s_options = new()
        {
            ThumbToolTipValueConverter = static v => s_percent.Get(Math.Clamp((int)MathF.Round(v * ClassicStageGeometry.PercentScale), 0, ClassicStageGeometry.PercentScaleMax), static p => p + "%"),
        };
        readonly FloatSignal _level = new(0f);
        readonly Signal<bool> _open = new(false);

        public override Element Render()
        {
            var p = UseProps<VolumeProps>();
            var t = p.T;
            UseSignalEffect(() => _level.SetIfChanged(t.Volume()));
            bool muted = t.Muted();
            bool open = p.Slider && _open.Value;
            var mute = ToolTip.Wrap(Glyph(muted ? Icons.Mute : Icons.Volume, t.ToggleMute, true) with
            {
                Key = "picture:bar:mute",
                OnFocusChanged = got => { if (got) _open.SetIfChanged(true); },
            }, Loc.Get(muted ? Strings.Player.Unmute : Strings.Player.Mute));
            var style = Slider.DefaultStyle with
            {
                RailFill = R.OnMedia.Ink with { A = ClassicStageGeometry.RailAlpha }, RailFillDisabled = R.OnMedia.Ink with { A = ClassicStageGeometry.RailDisabledAlpha },
                ValueFill = R.OnMedia.Ink, ValueFillPointerOver = R.OnMedia.Ink, ValueFillPressed = R.OnMedia.InkSecondary, ValueFillDisabled = R.OnMedia.InkTertiary,
                ThumbRing = R.OnMedia.Ink, ThumbFill = R.OnMedia.Ink, ThumbFillPointerOver = R.OnMedia.Ink, ThumbFillPressed = R.OnMedia.InkSecondary, ThumbFillDisabled = R.OnMedia.InkTertiary,
                ThumbBorder = GradientSpec.Solid(R.OnMedia.Stroke),
            };
            return new BoxEl
            {
                Key = "picture:bar:volume", Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f,
                OnHoverMove = _ => _open.SetIfChanged(true), OnPointerExit = () => _open.SetIfChanged(false),
                Children =
                [
                    mute,
                    new BoxEl
                    {
                        // where the row has no room for it the slider stays shut (the "…" menu and the player bar keep the volume): one tree either way
                        Key = "picture:bar:volume:slider", Width = open ? G.VolumeW : 0f, Height = G.Button, Shrink = 0f, ClipToBounds = true, HitTestVisible = p.Slider,
                        Direction = 0, AlignItems = FlexAlign.Center, Layout = s_reveal,
                        Children = [VolumeSlider.Create(_level, t.SetVolume, s_options, length: G.VolumeW, thickness: G.VolumeThickness, style: style)],
                    },
                ],
            };
        }
    }

    sealed record MenuProps(StageTransportProps T);

    /// <summary>The settings gear: the music video's own menu (the docked video's "…": where it plays, aspect ratio, quality, speed), built when it
    /// opens. It holds the idle chrome while open. Opened like the docked strip's menu (<c>ClassicOnMedia</c>): a flyout that may lease an OS popup
    /// window and take the window's acrylic. In the full screen the stage IS the monitor, so the menu always fits in the window and the engine keeps
    /// it in-window (<c>OverlayHost</c>'s fit gate): there it frosts the UI under it (the audio face), and over the playing picture, which no
    /// in-window backdrop can sample, it paints the acrylic's flat plate (the OpaqueSurface declaration).</summary>
    sealed class VideoMenuButton : Component
    {
        public override Element Render()
        {
            var t = UseProps<MenuProps>().T;
            var st = t.Stage;
            var anchor = UseRef<NodeHandle>(default);
            var handle = UseRef<OverlayHandle?>(null);
            var overlay = UseContext(Overlay.Service);
            void Toggle()
            {
                if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
                if (t.VideoMenu is not { } build) return;
                // OpaqueSurface is a DECLARATION that the plate sits over the video picture (the engine's acrylic cannot sample a video hole, so it
                // paints the flat plate there): true over a playing video, false over the audio face, whose cover and lyrics are ordinary UI the
                // in-window acrylic frosts. Decided at open time from the stage's own fact, never from geometry.
                bool overVideo = !st.AudioFace.Peek();
                var opened = overlay.Open(() => anchor.Value, () => MenuFlyout.Create(build(), () => handle.Value?.Close(), size: OverlayFit.MenuSize),
                    FlyoutPlacement.TopEdgeAlignedRight,
                    new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false, OpaqueSurface = overVideo });
                st.MenuOpen(true);
                opened.ClosedAction = () => { handle.Value = null; st.MenuOpen(false); };
                handle.Value = opened;
            }
            return ToolTip.Wrap(Glyph(Icons.Settings, Toggle, true, onRealized: h => anchor.Value = h) with { Key = "picture:bar:settings" }, Loc.Get(Strings.Stage.SettingsHeader));
        }
    }
}

/// <summary>The progress hairline while the bar rests: at the picture's foot (the stage's in Cinema, the 16:9 box's in Theatre, moving with it).</summary>
internal sealed class ClassicStageVideoHairline : Component
{
    public sealed record Props(StageRootView View);

    public override Element Render()
    {
        var v = UseProps<Props>().View;
        var r = ClassicStageTheatre.PictureRect(v.Stage.Layout.Value, v.ShowTheatre());
        return Stage.Layer with
        {
            Children =
            [
                new BoxEl
                {
                    Key = "picture:hairline", AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, Direction = 0, HitTestVisible = false,
                    Margin = new Edges4(r.X, r.Y + r.H - Stage.Layout.HairlineH, 0f, 0f), Width = r.W, Height = Stage.Layout.HairlineH, Layout = ClassicStageTheatre.Move,
                    Children = [new BoxEl { Grow = 1f, Height = Stage.Layout.HairlineH, Fill = Prop.Bind(v.Stage.Slab.Accent), TransformOriginX = 0f, TransformOriginY = ClassicStageGeometry.HairlineOriginY,
                                            Transform = Prop.Of(() => Affine2D.Scale(v.HairlineFraction(), 1f)) }],
                },
            ],
        };
    }
}
