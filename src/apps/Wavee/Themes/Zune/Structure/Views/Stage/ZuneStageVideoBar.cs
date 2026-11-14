// ── Themes/Zune/Structure/Views/Stage/ZuneStageVideoBar.cs — Zune's plate over the picture (Video mode) ───────────────────────────────────
//
// In Video mode the transport belongs to the PICTURE (YouTube's player bar, the owner's), drawn in Zune's language: the audio stage's own flat
// PLATE (the stage veil at the plate's 0.85, the tier's seek row and control row, the accent disc), at the foot of the picture's rect (the whole
// stage in Cinema, the 16:9 box in Theatre: it moves with the reservation on `ZuneStageTheatre.Move`), with a soft fade above it into the picture:
//
//   the seek line across the picture at the plate's inset
//   one row:  previous · the DISC · next · mute · the volume line   1:14 / 4:18
//             ............................................  heart · shuffle · repeat · this computer ▾ · "…" · settings · theatre · exit
//
// Plateless glyphs, the tier's targets; shuffle and repeat light in the accent ink, the heart fills in the accent at rest (state visible at rest).
// "…" is the now-playing menu; the settings glyph is the music video's own menu (`StageTransportProps.VideoMenu`); both hold the idle chrome. The
// row folds as the picture narrows (`ZuneStageVideoGeometry.Fold`): the volume line, then shuffle · repeat · the device word, the mute, the time.
// The parts are the audio plate's own (`ZuneStageTransport.Kit`): one look for one verb wherever it stands.

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
using Wavee.Theming.Roles;
using static FluentGpu.Dsl.Ui;

using G = Wavee.Themes.Zune.ZuneStageVideoGeometry;
using Ink = Wavee.Theming.Roles.StageInk;

namespace Wavee.Themes.Zune;

/// <summary>Zune's plate over the picture: its place (the picture's foot, moving with the reservation), the veil and the seek line. Its own
/// component, so a Cinema ⇄ Theatre change or a resize re-renders the plate alone.</summary>
internal sealed class ZuneStageVideoBar : Component
{
    public sealed record Props(StageRootView View);

    public override Element Render()
    {
        var v = UseProps<Props>().View;
        var st = v.Stage;
        var L = st.Layout.Value;
        var t = ZuneStageTiers.StageOf(TierContext.DrawnIn(UseContext(ThemeFactsContext.Signal)).Value.Tiers);
        bool theatre = v.ShowTheatre();
        var r = G.Picture(in L, t, theatre);
        float barH = G.BarH(t);
        float h = barH + G.PlateFadeH;
        float inset = G.BarInset(t, theatre);
        int fold = G.Fold(t, r.W, inset, L.TheatreFits);
        var tp = v.Transport;
        var veil = Ink.Veil;
        return Stage.Layer with
        {
            Children =
            [
                new BoxEl
                {
                    Key = "picture:bar", AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, ZStack = true, ClipToBounds = true, HitTestPassThrough = true,
                    Margin = new Edges4(r.X, r.Y + r.H - h, 0f, 0f), Width = r.W, Height = h, Layout = ZuneStageTheatre.Move,
                    Children =
                    [
                        // the flat plate, and the fade above it into the picture
                        new BoxEl
                        {
                            AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, HitTestVisible = false,
                            Gradient = GradientDown(new GradientStop(0f, veil with { A = 0f }), new GradientStop(G.PlateFadeH / h, veil with { A = G.PlateA }),
                                new GradientStop(1f, veil with { A = G.PlateA })),
                        },
                        new BoxEl
                        {
                            AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.Stretch, Direction = 1, Height = barH, HitTestPassThrough = true,
                            Padding = new Edges4(inset, 0f, inset, 0f),
                            Children =
                            [
                                new BoxEl
                                {
                                    Key = "picture:bar:seek", Height = t.SeekRowH, Direction = 0, AlignItems = FlexAlign.Center, HitTestPassThrough = true,
                                    Layout = ZuneStageTheatre.Move,
                                    Children =
                                    [
                                        new BoxEl
                                        {
                                            Grow = 1f, Shrink = 1f, MinWidth = 0f, OnHoverMove = _ => st.OverControls(true), OnPointerExit = () => st.OverControls(false),
                                            Children = [(tp.SeekBarWithChrome ?? tp.SeekBar)(st.Scrubbing)],
                                        },
                                    ],
                                },
                                Embed.Comp(new RowProps(tp, fold, L.TheatreFits, t), static () => new Row()),
                            ],
                        },
                    ],
                },
            ],
        };
    }

    sealed record RowProps(StageTransportProps T, int Fold, bool TheatreOffered, ZuneStageLayout Tier);

    /// <summary>The row: the playback facts are read here (a play, a like, a shuffle re-render the row and nothing above it).</summary>
    sealed class Row : Component
    {
        public override Element Render()
        {
            var p = UseProps<RowProps>();
            var tp = p.T;
            var t = p.Tier;
            var st = tp.Stage;
            var f = tp.Facts();
            var set = st.Accent.Value;
            var overlay = UseContext(Overlay.Service);
            var kit = new ZuneStageTransport.Kit(tp, f, set, overlay, ZuneStageTransport.Sizes.Of(t));
            int fold = p.Fold;
            LayoutTransition? slide = Design.Reduced ? null : ZuneMotion.PivotSlide;

            var left = new List<Element>(5)
            {
                kit.Previous() with { Key = "picture:bar:slot:prev" },
                kit.Play(G.Disc(t)) with { Key = "picture:bar:play" },
                kit.Next() with { Key = "picture:bar:slot:next" },
            };
            if (fold < G.FoldMute) left.Add(Folds(kit.Mute() with { Key = "picture:bar:slot:volume" }));
            if (fold < G.FoldSlider && t.VolumeW > 0f) left.Add(Folds(new BoxEl { Key = "picture:bar:slot:line", Direction = 0, AlignItems = FlexAlign.Center, Children = [kit.Volume()] }));

            var right = new List<Element>(9) { kit.Heart() with { Key = "picture:bar:slot:heart" } };
            if (fold < G.FoldSatellites)
            {
                right.Add(Folds(kit.Shuffle() with { Key = "picture:bar:slot:shuffle" }));
                right.Add(Folds(kit.Repeat() with { Key = "picture:bar:slot:repeat" }));
                right.Add(Folds(new BoxEl { Key = "picture:bar:slot:device", Direction = 0, AlignItems = FlexAlign.Center, MaxWidth = G.DeviceBudgetW, MinWidth = 0f, Shrink = 1f, Children = [kit.Device()] }));
            }
            right.Add(kit.More() with { Key = "picture:bar:more" });
            if (tp.VideoMenu is not null) right.Add(Embed.Comp(new MenuProps(tp, t), static () => new VideoMenuButton()) with { Key = "picture:bar:slot:settings" });
            if (p.TheatreOffered)
            {
                bool theatre = st.Theatre.Value;
                right.Add(Folds(ToolTip.Wrap(kit.Glyph(theatre ? Icons.FullScreen : Icons.SplitView, st.ToggleTheatre, true, Ink.InkSecondary) with { Key = "stage:bar:theatre" },
                    Loc.Get(theatre ? Strings.Stage.FillScreen : Strings.Stage.TheatreMode)) with { Key = "picture:bar:slot:theatre" }));
            }
            right.Add(ToolTip.Wrap(kit.Glyph(Icons.BackToWindow, st.Exit, true, Ink.InkSecondary) with { Key = "stage:bar:exit" }, Loc.Get(Strings.Stage.Exit)) with { Key = "picture:bar:slot:exit" });

            var row = new List<Element>(4) { Group(left.ToArray(), st.OverControls, t) with { Key = "picture:bar:transport", Layout = slide } };
            if (fold < G.FoldTime)
                row.Add(new BoxEl
                {
                    Key = "picture:bar:time", Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.XS, Margin = new Edges4(G.TimeGap, 0f, 0f, 0f), Shrink = 0f,
                    Enter = ZuneMotion.SwapFade, Exit = ZuneMotion.SwapFade, Transition = MotionTok.ControlNormal, Layout = slide,
                    Children = [(tp.TimeTextWithChrome ?? tp.TimeText)(false, Ink.Ink), Caption("/") with { Color = Ink.InkTertiary }, (tp.TimeTextWithChrome ?? tp.TimeText)(true, Ink.InkSecondary)],   // loc-allow: the times' own separator
                });
            row.Add(new BoxEl { Key = "picture:bar:spacer", Grow = 1f, MinWidth = G.TimeGap, HitTestVisible = false });
            row.Add(Group(right.ToArray(), st.OverControls, t) with { Key = "picture:bar:actions", Layout = slide });
            return new BoxEl
            {
                Key = "picture:bar:row", Height = t.PlateRowH, Direction = 0, AlignItems = FlexAlign.Center, HitTestPassThrough = true,
                Children = row.ToArray(),
            };
        }
    }

    /// <summary>A slot the row folds: it comes and goes as the picture widens or narrows, cross-fading on the theme's declared swap.</summary>
    static Element Folds(Element control) => control with { Enter = ZuneMotion.SwapFade, Exit = ZuneMotion.SwapFade, Transition = MotionTok.ControlNormal };

    /// <summary>A group of controls (no plate: the plate is the bar's); the pointer over it holds the chrome (the controls', never the empty row's).</summary>
    static BoxEl Group(Element[] kids, Action<bool> overControls, ZuneStageLayout t) => new()
    {
        Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Gap = t.RightGap,
        OnHoverMove = _ => overControls(true), OnPointerExit = () => overControls(false),
        Children = kids,
    };

    sealed record MenuProps(StageTransportProps T, ZuneStageLayout Tier);

    /// <summary>The settings glyph: the music video's own menu (where it plays, aspect ratio, quality, speed), built when it opens; it holds the
    /// idle chrome while open. Over the playing picture the flyout's plate is declared opaque (no in-window backdrop can sample a video hole).</summary>
    sealed class VideoMenuButton : Component
    {
        public override Element Render()
        {
            var p = UseProps<MenuProps>();
            var t = p.T;
            var st = t.Stage;
            var anchor = UseRef<NodeHandle>(default);
            var handle = UseRef<OverlayHandle?>(null);
            var overlay = UseContext(Overlay.Service);
            var kit = new ZuneStageTransport.Kit(t, default, st.Accent.Peek(), overlay, ZuneStageTransport.Sizes.Of(p.Tier));
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
            return ToolTip.Wrap(kit.Glyph(Icons.Settings, Toggle, true, Ink.InkSecondary) with { Key = "picture:bar:settings", OnRealized = h => anchor.Value = h },
                Loc.Get(Strings.Stage.SettingsHeader));
        }
    }
}

/// <summary>Zune's 2-px progress line while the plate rests: at the picture's foot (the stage's in Cinema, the 16:9 box's in Theatre, moving with it).</summary>
internal sealed class ZuneStageVideoHairline : Component
{
    public sealed record Props(StageRootView View);

    public override Element Render()
    {
        var v = UseProps<Props>().View;
        var t = ZuneStageTiers.StageOf(TierContext.DrawnIn(UseContext(ThemeFactsContext.Signal)).Value.Tiers);
        var r = G.Picture(v.Stage.Layout.Value, t, v.ShowTheatre());
        float lineH = ZuneStageGeometry.HairlineH;
        return Stage.Layer with
        {
            Children =
            [
                new BoxEl
                {
                    Key = "picture:hairline", AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, Direction = 0, HitTestVisible = false,
                    Margin = new Edges4(r.X, r.Y + r.H - lineH, 0f, 0f), Width = r.W, Height = lineH, Layout = ZuneStageTheatre.Move,
                    Children = [new BoxEl { Grow = 1f, Height = lineH, Fill = Prop.Bind(v.Stage.Slab.Accent), TransformOriginX = 0f, TransformOriginY = ZuneStageGeometry.HairlineOriginY,
                                            Transform = Prop.Of(() => Affine2D.Scale(v.HairlineFraction(), 1f)) }],
                },
            ],
        };
    }
}
