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
//
// A FADING FOOT (`Ramp`, provided by a surface that fades its canvas into what is under it: Classic's edge-to-edge canvas). The video fades
// along the ramp in the compositor (the window's backdrop shows where it has gone) and the poster fades with it in its own shader (its band
// from the ramp's start to its end, where its box stops), so neither needs an opaque ground painted over its foot.

using FluentGpu.Animation;
using FluentGpu.Controls.Media;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Pal;
using FluentGpu.Signals;

namespace Wavee;

public static partial class CanvasVideo
{
    /// <summary>The picture's alpha ramp down the layer (DIP from the layer's top; <see cref="VideoAlphaRamp"/>), for a surface that fades the
    /// canvas into what is under it instead of painting an opaque ground over its foot. Provided around the layer by that surface; null
    /// everywhere else, where the canvas is opaque. A stable signal: the surface writes it when its geometry changes, never per frame at rest.</summary>
    public static readonly Context<IReadSignal<VideoAlphaRamp>?> Ramp = new(null);

    /// <summary>The surface's canvas layer: a ZStack child that fills its box. <paramref name="scrim"/> (alpha &gt; 0) is drawn over the
    /// video, for a surface whose own scrim sits under it.</summary>
    public static Element Layer(Surface surface, CornerRadius4 corners = default, ColorF scrim = default, bool on = true)
        => Embed.Comp(new LayerProps(surface, corners, scrim, on), static () => new LayerView());

    /// <summary>The full-screen stage's canvas layer, over its backdrop and under its face: shown in the modes whose picture IS the backdrop
    /// (Lyrics, Queue) when the player style is Canvas. Under the visualizer and the artist photo the face covers the backdrop, so no canvas
    /// decodes there. <paramref name="scrim"/> is the stage's own veil, repeated over the video so the lyrics read as they read over the art.</summary>
    public static Element StageLayer(Stage.Mode mode, ColorF scrim)
        => Layer(Surface.Stage, default, scrim, on: StageShows(mode));

    /// <summary>Does the stage show a canvas in <paramref name="mode"/>? Only in the modes whose picture is the backdrop, and only while the
    /// player style is Canvas: Canvas is a player style, never a separate switch (owner, 2026-11-08), so the full-screen background follows the
    /// one choice the now-playing panel and Settings › Now playing make. Any other style keeps the blurred cover behind the lyrics (0.3.8 put
    /// the canvas there whatever the style, with no way to turn it off but the global Show Canvas setting). Reactive: reads the style's epoch,
    /// so the stage swaps its background the moment the style changes.</summary>
    public static bool StageShows(Stage.Mode mode)
        => mode is Stage.Mode.Lyrics or Stage.Mode.Queue && Rail.PlayerPrefs.Style() == Rail.PlayerCatalog.Canvas;

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
        // THE HAND-OFF. A presented frame is a hole in the swapchain the compositor fills, and a hole cannot fade: inside the exiting
        // stack's fade layer its erase lands in the layer, not the back buffer, so the old canvas used to vanish in one frame (the backdrop
        // or black for a frame, then the next picture). So a stack that is LEAVING is first drawn for one frame as its POSTER alone (the
        // canvas's own still, no video, so no hole), which lets the video go and the backdrop come back under it; the next frame drops it,
        // and its exit fade dissolves the still into whatever comes next (the next canvas fading in from its poster, or the stage's art).
        string _stackUri = "", _stackPoster = "";   // the stack on screen now
        string _parkUri = "", _parkPoster = "";     // the stack leaving, drawn poster-only for one frame
        bool _parkDrawn;
        readonly Signal<int> _drop = new(0);

        public override Element Render()
        {
            var p = UseProps<LayerProps>();
            var active = UseIsActive();
            var surface = p.Surface;
            var rampSig = UseContext(Ramp);
            var ramp = rampSig?.Value ?? default;   // the poster's band follows it (a re-render on a geometry change, never per frame)
            // A mounted, active surface may own the player; parked (KeepAlive) or minimized, it withdraws.
            // The claim follows (on, active) by deps: a render reads both, so a flip of either re-runs it (and its cleanup withdraws).
            bool claims = p.On && active.Value;
            UseEffect(() => claims ? Claim(surface) : null, DepKey.From(claims ? 1 : 0, (int)surface));
            bool on = p.On;
            _ = _drop.Value;   // the leaving stack's second frame

            var now = Current.Value;
            bool shows = on && now.Shows && Enabled();
            string uri = shows ? now.Uri : "";
            if (uri != _stackUri)
            {
                // the stack changes: the one on screen leaves through its poster (when it has one), the new one enters from its own
                if (_stackUri.Length > 0 && _stackPoster.Length > 0) { _parkUri = _stackUri; _parkPoster = _stackPoster; _parkDrawn = false; }
                _stackUri = uri;
            }
            if (shows) _stackPoster = now.Poster;
            Element? park = null;
            if (_parkUri.Length > 0 && !_parkDrawn)
            {
                park = Stack(_parkUri, _parkPoster, p, video: false, rampSig, ramp);
                _parkDrawn = true;
            }
            else if (_parkDrawn) { _parkUri = ""; _parkPoster = ""; _parkDrawn = false; }   // dropped: its exit fade carries the still out
            bool parked = park is not null;
            UseLayoutEffect(() => { if (parked) _drop.Value = _drop.Peek() + 1; }, DepKey.From(parked ? 1 : 0, _parkUri.GetHashCode()));

            if (!shows && park is null)
                return FillBox with { HitTestVisible = false };

            var stacks = new List<Element>(2);
            if (park is not null) stacks.Add(park);
            if (shows) stacks.Add(Stack(now.Uri, now.Poster, p, video: now.File.Length > 0 && Presents(surface), rampSig, ramp));
            return FillBox with { HitTestVisible = false, Children = stacks.ToArray() };
        }

        /// <summary>One canvas's stack (keyed by its uri, fading in on entry and out on exit): its poster, its video when it presents, the
        /// surface's scrim. Without the video the poster always draws (the leaving frame); with it, the hole erases it, so it stops drawing
        /// once a frame is presented. Under a <see cref="Ramp"/> (<paramref name="ramp"/>, down the layer) the poster's box stops at the ramp's
        /// end, its top held where the full box puts it, and its own band fades it from the ramp's start, as the video fades.</summary>
        static Element Stack(string uri, string poster, LayerProps p, bool video, IReadSignal<VideoAlphaRamp>? rampSig, VideoAlphaRamp ramp)
        {
            var surface = p.Surface;
            var kids = new List<Element>(3);
            bool foot = !ramp.IsNone && ramp.Axis == VideoRampAxis.TopToBottom && ramp.To > ramp.From;
            if (poster.Length > 0)
                kids.Add(new ImageEl
                {
                    Key = "canvas-poster",
                    // The still's own height (the largest .thmb is 288x512): decoded once at its size, never at the box's.
                    Source = poster, Fit = ImageFit.Cover, Corners = p.Corners, DecodePx = PosterDecodePx,
                    AlignSelf = foot ? FlexAlign.Start : FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch,
                    // Under a ramp: the box ends where the picture has gone, the crop's top where the full box has it (a 9:16 still in the
                    // 9:16 layer is scaled by the width in both), and the band fades it from the ramp's start.
                    Height = foot ? ramp.To : float.NaN,
                    FocusY = foot ? 0f : 0.5f, FocusFrameY = foot ? 0f : 0.5f,
                    Mask = foot ? new ImageMaskSpec(EdgeMask.Bottom, 0f, 0f, 0f, ramp.To - ramp.From) : null,
                    RevealTransition = ImageTransition.Fade(Design.Motion.Fast),
                    // Under a presented frame the poster is erased by the hole every frame: stop drawing it at all.
                    Visible = video ? Prop.Of(() => Presented.Value != surface) : true,
                });
            if (video)
                kids.Add(Embed.Comp(new VideoProps(surface, p.Corners.TopLeft, rampSig), static () => new VideoView()) with { Key = "canvas-video" });
            if (p.Scrim.A > 0f)
                kids.Add(new BoxEl { AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, HitTestVisible = false, Fill = p.Scrim, BrushTransitionMs = Stage.Tone.CrossFadeMs });
            return FillBox with
            {
                Key = "canvas:" + uri,
                ClipToBounds = true,
                Corners = p.Corners, HitTestVisible = false,
                Enter = new EnterExit(Opacity: 0f, Active: true), Exit = new EnterExit(Opacity: 0f, Active: true),
                Transition = MotionTok.StandardEnter,
                Children = kids.ToArray(),
            };
        }
    }

    internal sealed record VideoProps(Surface Surface, float Radius, IReadSignal<VideoAlphaRamp>? Ramp);

    /// <summary>The element that presents the one player: binds on mount, lets go on unmount (the owner hand-off, Canvas.cs).</summary>
    sealed class VideoView : Component
    {
        public override Element Render()
        {
            var p = UseProps<VideoProps>();
            var surface = p.Surface;
            var occluded = UseContext(InputHooks.Current).WindowOccluded;   // THIS element's window (the mini player's is not the main one)
            UseEffect(() =>
            {
                BindSurface(surface);
                BindWindow(occluded);
                return () => UnbindSurface(surface);
            }, DepKey.Empty);
            float radius = p.Radius;
            var ramp = p.Ramp;   // a stable signal per surface: the element watches it (frozen at the element's mount, as its corners)
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
                AlphaRamp = ramp,
                InViewChanged = NoteInView,
                SurfacePresented = () => Presented.Value = surface,
            })],
            };
        }
    }
}
