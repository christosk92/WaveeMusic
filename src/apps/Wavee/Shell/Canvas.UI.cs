// ── Shell/Canvas.UI.cs — the Canvas layer a surface stacks over its art (a named partial of Shell/Canvas.cs) ─────────────────────────────
//
// THE CONTRACT WITH A SURFACE. `Canvas.Layer(surface, corners)` is one ZStack layer that FILLS whatever box the surface gives it and never
// sizes it: the surface's layout is exactly what it is without Canvas, with or without a canvas, before and after it lands (no jump, no
// empty frame). Without a canvas (none, unknown yet, the setting off, a music video on the video host) the layer draws NOTHING.
//
// WITH ONE, three layers fade in over the art (MotionTok.StandardEnter):
//   · the poster: the canvas's own still (the .thmb jpg), cropped to the box, shown until the first video frame;
//   · the video: a decorative MediaPlayerElement (no chrome, no poster, transparent until its first frame) over the poster, cropped to the
//     box (UniformToFill) — mounted only on the surface that OWNS the one player (Canvas.cs) once the previous presenter let go;
//   · the surface's scrim, when it has one (the stage's veil), so its text reads over the video as it reads over the art.
// A track change keys the stack per track: the old one fades out, the new one fades in from its poster.

using FluentGpu.Animation;
using FluentGpu.Controls.Media;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace Wavee;

public static partial class CanvasVideo
{
    /// <summary>The surface's canvas layer: a ZStack child that fills its box. <paramref name="scrim"/> (alpha &gt; 0) is drawn over the
    /// video, for a surface whose own scrim sits under it.</summary>
    public static Element Layer(Surface surface, CornerRadius4 corners = default, ColorF scrim = default, bool on = true)
        => Embed.Comp(new LayerProps(surface, corners, scrim, on), static () => new LayerView());

    /// <summary>The full-screen stage's canvas layer, over its backdrop and under its face: shown in the modes whose picture IS the backdrop
    /// (Lyrics, Queue). Under the visualizer and the artist photo the face covers the backdrop, so no canvas decodes there.
    /// <paramref name="scrim"/> is the stage's own veil, repeated over the video so the lyrics read as they read over the art.</summary>
    public static Element StageLayer(Stage.Mode mode, ColorF scrim)
        => Layer(Surface.Stage, default, scrim, on: StageShows(mode));

    /// <summary>The stage modes a canvas shows in.</summary>
    public static bool StageShows(Stage.Mode mode) => mode is Stage.Mode.Lyrics or Stage.Mode.Queue;

    /// <summary>A box that fills a ZStack it is placed in, also as a COMPONENT ROOT (the anchor mirrors the root's participation:
    /// Stretch fills the width, Grow + Basis 0 the height — <c>Stage.Layer</c>'s shape).</summary>
    static readonly BoxEl FillBox = new()
    {
        AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, MinHeight = 0f,
        ZStack = true,
    };

    /// <summary>The poster's decode size: the tallest .thmb rendition (512).</summary>
    const float PosterDecodePx = 512f;

    internal sealed record LayerProps(Surface Surface, CornerRadius4 Corners, ColorF Scrim, bool On);

    sealed class LayerView : Component
    {
        public override Element Render()
        {
            var p = UseProps<LayerProps>();
            var active = UseIsActive();
            var surface = p.Surface;
            // A mounted, active surface may own the player; parked (KeepAlive) or minimized, it withdraws.
            // The claim follows (on, active) by deps: a render reads both, so a flip of either re-runs it (and its cleanup withdraws).
            bool claims = p.On && active.Value;
            UseEffect(() => claims ? Claim(surface) : null, DepKey.From(claims ? 1 : 0, (int)surface));
            bool on = p.On;

            var now = Current.Value;
            if (!on || !now.Shows || !Enabled())
                return FillBox with { HitTestVisible = false };

            var kids = new List<Element>(3);
            if (now.Poster.Length > 0)
                kids.Add(new ImageEl
                {
                    Key = "canvas-poster",
                    // The still's own height (the largest .thmb is 288x512): decoded once at its size, never at the box's.
                    Source = now.Poster, Fit = ImageFit.Cover, Corners = p.Corners, DecodePx = PosterDecodePx,
                    AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch,
                    RevealTransition = ImageTransition.Fade(Design.Motion.Fast),
                    // Under a presented frame the poster is erased by the hole every frame: stop drawing it at all.
                    Visible = Prop.Of(() => Presented.Value != surface),
                });
            if (now.File.Length > 0 && Presents(surface))
                kids.Add(Embed.Comp(new VideoProps(surface, p.Corners.TopLeft), static () => new VideoView()) with { Key = "canvas-video" });
            if (p.Scrim.A > 0f)
                kids.Add(new BoxEl { AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, HitTestVisible = false, Fill = p.Scrim, BrushTransitionMs = Stage.Tone.CrossFadeMs });

            return FillBox with
            {
                HitTestVisible = false,
                Children =
                [
                    FillBox with
                    {
                        Key = "canvas:" + now.Uri,
                        ClipToBounds = true,
                        Corners = p.Corners, HitTestVisible = false,
                        Enter = new EnterExit(Opacity: 0f, Active: true), Exit = new EnterExit(Opacity: 0f, Active: true),
                        Transition = MotionTok.StandardEnter,
                        Children = kids.ToArray(),
                    },
                ],
            };
        }
    }

    internal sealed record VideoProps(Surface Surface, float Radius);

    /// <summary>The element that presents the one player: binds on mount, lets go on unmount (the owner hand-off, Canvas.cs).</summary>
    sealed class VideoView : Component
    {
        public override Element Render()
        {
            var p = UseProps<VideoProps>();
            var surface = p.Surface;
            UseEffect(() =>
            {
                BindSurface(surface);
                return () => UnbindSurface(surface);
            }, DepKey.Empty);
            float radius = p.Radius;
            // A column that fills the layer; the element grows down it (its root is Grow 1, and a decorative one has no minimum).
            return FillBox with
            {
                ZStack = false, Direction = 1, HitTestVisible = false,
                Children = [Embed.Comp(() => new MediaPlayerElement
            {
                Player = Player,
                IsDecorative = true,
                AreTransportControlsEnabled = false,
                ShowStatusOverlay = false,
                ShowLetterboxBars = false,
                Stretch = MediaStretch.UniformToFill,
                CornerRadius = radius,
                InViewChanged = NoteInView,
                SurfacePresented = () => Presented.Value = surface,
            })],
            };
        }
    }
}
