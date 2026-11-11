// ── Themes/WinUi/Structure/Views/Stage/WinUiStageVideoBar.cs — WinUI's command bar over the picture (Video mode) ───────────────────────────
//
// The owner's player bar (YouTube's order) as a Windows 11 media command bar: at the foot of the picture's rect (the whole stage in Cinema, the 16:9
// box in Theatre; it moves with the reservation on `WinUiStageTheatre.Move`), inset from its edges, ONE r8 plate (the stage card's acrylic fallback as
// smoke: no acrylic can sample the video hole, see `WinUiStageGeometry.Video.cs`), a 1 px stroke and the card's shadow:
//
//   the seek rail across the top of the plate
//   [ ◉ play · previous · next · mute (+ the slider it reveals on hover or focus) ] | 0:05 / 3:04  .........  [ ♥ · shuffle · repeat · device ] | [ "…" · settings · Cinema/Theatre · exit ]
//
// Every command is a 40 target with WinUI's subtle hover and press plates (an AppBarButton); play is the stage's accent disc; shuffle and repeat show
// their latched state in the accent glyph and the heart its saved state at rest; AppBarSeparators split the groups. "…" is the now-playing menu
// (`Stage.NowPlayingMenu`); the gear is the music video's own menu (`StageTransportProps.VideoMenu`). Both hold the idle chrome while open. The row
// folds as the picture narrows (`WinUiStageVideoBarGeometry.Fold`). The plate holds the chrome while the pointer is on it (the bar's controls, never
// the picture around it). Ink is the theme's arm: the plate carries its own contrast over any picture.

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

using B = Wavee.Themes.WinUi.WinUiStageVideoBarGeometry;
using C = Wavee.Themes.WinUi.WinUiStageCinemaGeometry;
using Ink = Wavee.Theming.Roles.StageInk;

namespace Wavee.Themes.WinUi;

/// <summary>WinUI's command bar over the picture: its place (the picture's foot, moving with the reservation), the plate, the seek rail and the row.
/// Its own component, so a Cinema ⇄ Theatre change or a resize re-renders the bar alone.</summary>
internal sealed class WinUiStageVideoBar : Component
{
    public sealed record Props(StageRootView View);

    static readonly FormatCache<int> s_percent = FormatCache.Create<int>();

    public override Element Render()
    {
        var v = UseProps<Props>().View;
        var t = WinUiStageTheatre.Tier(UseContext(ThemeFactsContext.Signal));
        var st = v.Stage;
        var L = st.Layout.Value;
        bool theatre = v.ShowTheatre();
        var r = WinUiStageTheatre.PictureRect(L, t, theatre);
        float inset = B.Inset(t.BarInset, theatre);
        int fold = B.Fold(r.W, inset, L.TheatreFits);
        var overControls = st.OverControls;
        var tp = v.Transport;
        return Stage.Layer with
        {
            Children =
            [
                new BoxEl
                {
                    Key = "picture:bar", AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, Direction = 1, HitTestPassThrough = false,
                    Margin = new Edges4(r.X + inset, r.Y + r.H - inset - B.CardH, 0f, 0f), Width = MathF.Max(0f, r.W - 2f * inset), Height = B.CardH,
                    Padding = new Edges4(B.PadX, B.PadTop, B.PadX, B.PadBottom), Layout = WinUiStageTheatre.Move,
                    Corners = Radii.CardAll, Fill = Plate(), BorderWidth = 1f, BorderColor = Ink.Stroke, Shadow = Elevation.Card,
                    OnHoverMove = _ => overControls(true), OnPointerExit = () => overControls(false),
                    Children =
                    [
                        new BoxEl
                        {
                            Key = "picture:bar:seek", Height = B.SeekH, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center,
                            Padding = new Edges4(B.SeekInsetX - B.PadX, 0f, B.SeekInsetX - B.PadX, 0f),
                            Children = [new BoxEl { Grow = 1f, Shrink = 1f, MinWidth = 0f, Children = [(tp.SeekBarWithChrome ?? tp.SeekBar)(st.Scrubbing)] }],
                        },
                        Embed.Comp(new RowProps(tp, fold, L.TheatreFits), static () => new Row()),
                    ],
                },
            ],
        };
    }

    /// <summary>The smoke plate over the picture: the stage card's acrylic fallback at <see cref="WinUiStageCinemaGeometry.PlateA"/>.</summary>
    internal static ColorF Plate() => Ink.Card.Fallback with { A = C.PlateA };

    sealed record RowProps(StageTransportProps T, int Fold, bool TheatreOffered);

    /// <summary>The row: the playback facts are read here (a play, a like, a shuffle re-render the row and nothing above it).</summary>
    sealed class Row : Component
    {
        public override Element Render()
        {
            var p = UseProps<RowProps>();
            var tp = p.T;
            var st = tp.Stage;
            var f = tp.Facts();
            var verbs = tp.Verbs;
            var set = st.Accent.Value;
            var overlay = UseContext(Overlay.Service);
            var t = WinUiStageTheatre.Tier(UseContext(ThemeFactsContext.Signal));
            var menuOpen = st.MenuOpen;
            int fold = p.Fold;

            var left = new List<Element>(5)
            {
                K("play", WinUiStageTransport.Play(f.Playing, f.PrimaryEnabled, B.Disc, B.DiscGlyph, set, verbs) with { Key = "picture:bar:play" }),
                K("prev", ToolTip.Wrap(WinUiStageTransport.Glyph(Icons.Previous, verbs.Previous, B.Button, B.Glyph, f.PrevEnabled), Loc.Get(Strings.Player.Previous))),
                K("next", ToolTip.Wrap(WinUiStageTransport.Glyph(Icons.Next, verbs.Next, B.Button, B.Glyph, f.NextEnabled), Loc.Get(Strings.Player.Next))),
            };
            if (fold < B.FoldMute) left.Add(Fold("volume", Embed.Comp(new VolumeProps(tp, set, fold < B.FoldSlider), static () => new Volume())));
            if (fold < B.FoldTime)
            {
                left.Add(Fold("time:sep", Separator()));
                left.Add(Fold("time", new BoxEl
                {
                    Height = B.Button, Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.XS, Padding = new Edges4(B.TimePadX, 0f, B.TimePadX, 0f), Shrink = 0f,
                    HitTestVisible = false,
                    Children = [(tp.TimeTextWithChrome ?? tp.TimeText)(false, Ink.Ink), Caption("/") with { Color = Ink.InkSecondary }, (tp.TimeTextWithChrome ?? tp.TimeText)(true, Ink.InkSecondary)],   // loc-allow: the times' own separator
                }));
            }

            var right = new List<Element>(10) { K("heart", Heart(f.Saved, f.Like, set.Fill)) };
            if (fold < B.FoldSatellites)
            {
                right.Add(Fold("shuffle", ToolTip.Wrap(WinUiStageTransport.Satellite(Icons.Shuffle, verbs.ToggleShuffle, f.CanTransport, f.Shuffle, set.Fill, B.Button, B.Glyph), Loc.Get(Strings.Player.Shuffle))));
                right.Add(Fold("repeat", ToolTip.Wrap(WinUiStageTransport.Satellite(f.RepeatOne ? Icons.RepeatOne : Icons.RepeatAll, verbs.CycleRepeat, f.CanTransport, f.RepeatOn, set.Fill, B.Button, B.Glyph), Loc.Get(Strings.Player.Repeat))));
                right.Add(Fold("device", Embed.Comp(new WinUiStageTransport.DeviceButton.Props(tp, t, Labelled: false, Box: B.Button, GlyphSize: B.Glyph), static () => new WinUiStageTransport.DeviceButton())));
            }
            right.Add(K("sep", Separator()));
            right.Add(K("more", ToolTip.Wrap((WinUiStageTransport.Glyph(Icons.More, null, B.Button, B.Glyph, enabled: true, clickRequestsContext: true) with { Key = "picture:bar:more" })
                .WithContextMenu(overlay,
                    () => { var m = Stage.NowPlayingMenu?.Invoke(); if (m is { } model && ContextMenu.HasAnyEnabled(model)) menuOpen(true); return m; },
                    new ContextMenuOptions { OnClosed = _ => menuOpen(false) }), Loc.Get(Strings.Common.More))));
            if (tp.VideoMenu is not null) right.Add(K("settings", Embed.Comp(new MenuProps(tp), static () => new VideoMenuButton())));
            if (p.TheatreOffered)
            {
                bool theatre = st.Theatre.Value;
                right.Add(Fold("theatre", ToolTip.Wrap(WinUiStageTransport.Glyph(theatre ? Icons.FullScreen : Icons.SplitView, st.ToggleTheatre, B.Button, B.Glyph) with { Key = "stage:bar:theatre" },
                    Loc.Get(theatre ? Strings.Stage.FillScreen : Strings.Stage.TheatreMode))));
            }
            right.Add(K("exit", ToolTip.Wrap(WinUiStageTransport.Glyph(Icons.BackToWindow, st.Exit, B.Button, B.Glyph) with { Key = "stage:bar:exit" }, Loc.Get(Strings.Stage.Exit))));

            return new BoxEl
            {
                Key = "picture:bar:row", Height = B.RowH, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Layout = WinUiStageTheatre.Move,
                Children =
                [
                    new BoxEl { Key = "picture:bar:transport", Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f, Children = left.ToArray() },
                    new BoxEl { Key = "picture:bar:spacer", Grow = 1f, MinWidth = B.MinSpread, HitTestVisible = false },
                    new BoxEl { Key = "picture:bar:actions", Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f, Children = right.ToArray() },
                ],
            };
        }
    }

    /// <summary><paramref name="control"/> keyed as its slot (a fold that adds or drops a command never shifts the ones after it into another slot).</summary>
    static Element K(string slot, Element control) => control with { Key = "picture:bar:slot:" + slot };

    /// <summary>A slot the row folds: it cross-fades as WinUI's declared swap when the picture widens or narrows.</summary>
    static Element Fold(string slot, Element control) => K(slot, control) with
    {
        Enter = C.VideoSwap, Exit = C.VideoSwap, Transition = C.VideoSwapMotion,
    };

    /// <summary>The AppBarSeparator: a 1 x 20 stroke, 4 clear on each side.</summary>
    static Element Separator() => new BoxEl
    {
        Width = B.SepW, Height = B.SepH, Shrink = 0f, Margin = new Edges4(B.SepGap, 0f, B.SepGap, 0f), Fill = Ink.Stroke, HitTestVisible = false,
    };

    /// <summary>The heart: the accent glyph while the track is saved (visible at rest).</summary>
    static Element Heart(bool saved, Action? onLike, ColorF accent) => WinUiStageTransport.Glyph(saved ? Icons.HeartFill : Icons.Heart, onLike, B.Button, B.Glyph, onLike is not null) with
    {
        Key = "picture:bar:heart", BlocksDragArm = true,
        Children = [new BoxEl { Key = saved ? "sh:on" : "sh:off", Children = [new TextEl(saved ? Icons.HeartFill : Icons.Heart) { Size = B.Glyph, FontFamily = Tok.Definition.Fonts.Icon, Color = saved ? accent : Ink.Ink }] }],
    };

    sealed record VolumeProps(StageTransportProps T, AccentSet Set, bool Slider);

    /// <summary>The mute command and, where the row has room, the volume slider it reveals while the pointer is over the group or the mute has
    /// keyboard focus: its width eases open and shut, pushing the time along. Its own component: a mute, a drag or the reveal re-render the group alone.</summary>
    sealed class Volume : Component
    {
        static readonly LayoutTransition s_reveal = new(TransitionChannels.Bounds, TransitionDynamics.Tween(B.VolumeRevealMs, Easing.FluentDecelerate), SizeMode.Relayout);
        static readonly Slider.SliderOptions s_options = new()
        {
            ThumbToolTipValueConverter = static v => s_percent.Get(Math.Clamp((int)MathF.Round(v * WinUiStageGeometry.PercentScale), 0, WinUiStageGeometry.PercentScaleMax), static p => p + "%"),
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
            var mute = ToolTip.Wrap(WinUiStageTransport.Glyph(muted ? Icons.Mute : Icons.Volume, t.ToggleMute, B.Button, B.Glyph) with
            {
                Key = "picture:bar:mute",
                OnFocusChanged = got => { if (got) _open.SetIfChanged(true); },
            }, Loc.Get(muted ? Strings.Player.Unmute : Strings.Player.Mute));
            var style = Slider.DefaultStyle with
            {
                RailFill = Ink.Ink with { A = WinUiStageGeometry.RailAlpha }, RailFillDisabled = Ink.Ink with { A = WinUiStageGeometry.RailDisabledAlpha },
                ValueFill = p.Set.Fill, ValueFillPointerOver = p.Set.FillSecondary, ValueFillPressed = p.Set.FillTertiary, ValueFillDisabled = p.Set.Fill with { A = WinUiStageGeometry.ThumbDisabledAlpha },
                ThumbRing = Tok.FillControlSolid, ThumbFill = p.Set.Fill, ThumbFillPointerOver = p.Set.FillSecondary, ThumbFillPressed = p.Set.FillTertiary, ThumbFillDisabled = p.Set.Fill with { A = WinUiStageGeometry.ThumbDisabledAlpha },
                ThumbBorder = GradientSpec.Solid(Ink.Stroke),
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
                        Key = "picture:bar:volume:slider", Width = open ? B.VolumeW : 0f, Height = B.Button, Shrink = 0f, ClipToBounds = true, HitTestVisible = p.Slider,
                        Direction = 0, AlignItems = FlexAlign.Center, Layout = s_reveal,
                        Children = [VolumeSlider.Create(_level, t.SetVolume, s_options, length: B.VolumeW, thickness: B.VolumeThickness, style: style)],
                    },
                ],
            };
        }
    }

    sealed record MenuProps(StageTransportProps T);

    /// <summary>The settings gear: the music video's own menu (where it plays, aspect ratio, quality, speed), built when it opens; it holds the idle
    /// chrome while open. A flyout that may lease an OS popup window; kept in-window over the playing picture it paints its acrylic's flat plate
    /// (the OpaqueSurface declaration), over the audio face it frosts the UI under it.</summary>
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
                bool overVideo = !st.AudioFace.Peek();
                var opened = overlay.Open(() => anchor.Value, () => MenuFlyout.Create(build(), () => handle.Value?.Close(), size: OverlayFit.MenuSize),
                    FlyoutPlacement.TopEdgeAlignedRight,
                    new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false, OpaqueSurface = overVideo });
                st.MenuOpen(true);
                opened.ClosedAction = () => { handle.Value = null; st.MenuOpen(false); };
                handle.Value = opened;
            }
            return ToolTip.Wrap(WinUiStageTransport.Glyph(Icons.Settings, Toggle, B.Button, B.Glyph) with { Key = "picture:bar:settings", OnRealized = h => anchor.Value = h },
                Loc.Get(Strings.Stage.SettingsHeader));
        }
    }
}
