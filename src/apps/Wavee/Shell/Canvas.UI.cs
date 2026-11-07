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

namespace Wavee;

public static partial class CanvasVideo
{
    /// <summary>The surface's canvas layer: a ZStack child that fills its box. <paramref name="scrim"/> (alpha &gt; 0) is drawn over the
    /// video, for a surface whose own scrim sits under it.</summary>
    public static Element Layer(Surface surface, CornerRadius4 corners = default, ColorF scrim = default)
        => Embed.Comp(new LayerProps(surface, corners, scrim), static () => new LayerView());

    internal sealed record LayerProps(Surface Surface, CornerRadius4 Corners, ColorF Scrim);

    sealed class LayerView : Component
    {
        public override Element Render()
        {
            var p = UseProps<LayerProps>();
            var active = UseIsActive();
            var surface = p.Surface;
            // A mounted, active surface may own the player; parked (KeepAlive) or minimized, it withdraws.
            UseEffect(() => active.Value ? Claim(surface) : null);

            var now = Current.Value;
            if (!now.Shows || !Enabled())
                return new BoxEl { AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, HitTestVisible = false };

            var kids = new List<Element>(3);
            if (now.Poster.Length > 0)
                kids.Add(new ImageEl
                {
                    Key = "canvas-poster",
                    Source = now.Poster, Fit = ImageFit.Cover, Corners = p.Corners,
                    AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch,
                    RevealTransition = ImageTransition.Fade(Design.Motion.Fast),
                });
            if (now.File.Length > 0 && Presents(surface))
                kids.Add(Embed.Comp(new VideoProps(surface, p.Corners.TopLeft), static () => new VideoView()) with { Key = "canvas-video" });
            if (p.Scrim.A > 0f)
                kids.Add(new BoxEl { AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, HitTestVisible = false, Fill = p.Scrim });

            return new BoxEl
            {
                AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, ZStack = true, HitTestVisible = false,
                Children =
                [
                    new BoxEl
                    {
                        Key = "canvas:" + now.Uri,
                        AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, ZStack = true, ClipToBounds = true,
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
            return new BoxEl
            {
                AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, Direction = 1, HitTestVisible = false,
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
            })],
            };
        }
    }
}
