// ── Themes/Classic/Structure/Views/Panel/ClassicBleed.cs — Classic's edge-to-edge canvas ───────────────────────────────────────────────
//
// The Canvas player style at rest fills the panel from its very top (`NowPlayingHeroProps.BleedVideo`, core) and dissolves into the panel:
// over its last stretch the picture fades, on an eased curve, into the panel's own plain ground, the same ground the panel shows with the
// canvas off (the owner, 2026-11-08: "theres too much color. it should just be plain"). Nothing moves there and nothing is tinted by the art
// (the owner, 2026-11-10: "get rid of the wave effect, just fade it beautifully"): the art lives in the picture and in the meta's accent text.
//
// THE PICTURE FADES ITSELF. The video is a plane UNDER the UI, so an opaque ground painted over it could never be the panel's real ground
// (the content layer over the material's tint over live Mica, whose wallpaper colour the app cannot sample: the owner, 2026-11-11, "the fade
// here with the canvas is just black/dark. no color", then "this bleed here is still just dark. compare it to [the normal panel]"). So the
// compositor fades the picture (`CanvasVideo.Ramp`, the presenter's alpha ramp: the curve's seven stops as the picture's alpha), the canvas
// is cut where it has gone, and below that nothing is painted: the panel is exactly the panel the Cover style shows. Over the fade, where
// the picture's hole erased the panel's own layers, two gradients paint them back on the complementary curve as one translucent colour (the
// content layer over the material's tint, `ColorContrast.Over`), so the picture is not veiled at the top and the panel is whole where the
// picture has gone. Not repainted there: Home's radial washes (the fade's 200 DIP only; below it the real layers show).
//
// THE STAND-IN (`ClassicBleedInk.Ground`): the same panel flattened over the backdrop's opaque stand-in `Tok.WindowBackground`, the closest
// known colour to what the glass shows. It is what the meta's accent text is solved against, the head scrim's shade in the dark kind, and
// the entrance veil (the one place a colour must hide the picture: the first frames after the style picker shuts).

using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;
using VideoAlphaRamp = FluentGpu.Pal.VideoAlphaRamp;
using VideoAlphaStop = FluentGpu.Pal.VideoAlphaStop;
using VideoRampAxis = FluentGpu.Pal.VideoRampAxis;

namespace Wavee.Themes.Classic;

/// <summary>The colours of the canvas's meta: the panel's ground flattened over the backdrop's stand-in (<paramref name="Ground"/>, the file
/// header's stand-in: the accent's solve, the head scrim's shade, the entrance veil), and the meta's accent text (<paramref name="Text"/>: the
/// artist, the heart).</summary>
internal readonly record struct ClassicBleedInk(ColorF Ground, ColorF Text)
{
    /// <summary>The material's colour under the panel: the presented state's tint as the material layer paints it
    /// (<see cref="ClassicMaterial.TintFill"/>), or the neutral ground with no frame. Tracked reads (the state at navigation rate, the
    /// Surfaces option): the caller re-renders when the page's tint or the option changes, never per frame.</summary>
    public static ColorF Tint(IReadSignal<ShellMaterialState>? state, IOptionReader? options)
        => state is null ? R.Wash.NeutralGround : ClassicMaterial.TintFill(state.Value, ClassicMaterial.Level(options));

    /// <summary>The colours for the cover at <paramref name="url"/> over the material <paramref name="tint"/> (<see cref="Tint"/>). The ground
    /// is the panel's own, flattened: the content layer over the tint over the window's backdrop's opaque stand-in, the closest known colour
    /// to what the panel shows. The accent text is the cover's dominant hue (its grading, the house palette) solved as text on that ground; a cover with no
    /// grading yet reads in the neutral ink. The palette watch is read inside, so a late grading re-renders the reader.</summary>
    public static ClassicBleedInk Of(string? url, ColorF tint)
    {
        if (url is { Length: > 0 }) _ = Palette.Watch(url).Value;
        bool light = R.IsLight;
        ColorF ground = ColorContrast.Flatten(R.Layer.FileArea, ColorContrast.Flatten(tint, Tok.WindowBackground));
        ColorF dominant = ColorContrast.Flatten(Tok.TextSecondary, ground);   // the neutral ink, opaque (the solve keeps its alpha)
        if (url is { Length: > 0 } && (Palette.TryScheme(url, light, out Scheme s) || Palette.TryScheme(url, !light, out s)))
            dominant = ColorScience.Accent(s);
        // The accent text: the dominant hue solved as text on the ground, a step past AA (the rendered glyphs blend a little under the solve).
        ColorF text = ColorScience.Hairline(dominant, light ? ThemeKind.Light : ThemeKind.Dark, ground, ClassicHeroLayout.ArtTextContrast);
        if (!ColorContrast.MeetsAaText(text, ground)) text = ColorContrast.PickContrast(ground);
        return new ClassicBleedInk(ground, text);
    }
}

internal sealed record ClassicBleedProps(NowPlayingHeroProps NowPlaying, float Width);

/// <summary>The edge-to-edge canvas with its fade (a component: the ground follows the theme without re-rendering the panel).</summary>
internal sealed class ClassicBleed : Component
{
    /// <summary>The entrance veil (inspection reads its opacity on the frames after the picker shuts).</summary>
    public const string VeilKey = "classic-bleed-veil";

    static readonly EnterExit Fade = new(Opacity: 0f, Active: true);
    Action<NodeHandle>? _lift;

    /// <summary>The fade gradients' cross-fade to a page's new tint. The material layer fades its tint on its brush transition (linear over
    /// <see cref="Design.Motion.Standard"/>, from the colour it shows); a gradient has no brush channel, so the two halves carry the old
    /// colour as <c>Gradient</c>, the new one as <c>GradientTo</c>, and a <c>GradientMix</c> on the frame clock walks the same line in
    /// step (an opacity cross-fade of two gradient layers dips their alpha: never exact).</summary>
    sealed class GroundFade
    {
        public ColorF From, To;
        public long StartQpc;
        public int Epoch;
        public bool Fading, Light;
        public TimerHandle Done;
        public Action? Settle;
        public Func<float>? MixNow;

        /// <summary>The cross-fade's progress on this frame's clock: linear, as the plates' brush fade.</summary>
        public float Mix()
        {
            if (!Fading) return 1f;
            double ms = (FrameClock.FrameQpc - StartQpc) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            return Math.Clamp((float)(ms / Design.Motion.Standard), 0f, 1f);
        }

        /// <summary>The ground the gradients show now (a retarget starts from it, as the plates' fade does).</summary>
        public ColorF Shown => Fading ? ColorF.Lerp(From, To, Mix()) : To;
    }

    public override Element Render()
    {
        // The entrance is a veil of the stand-in ground lifting off the picture (1 -> 0 on the entrance token), never a fade of the stack
        // itself: the video is a plane UNDER the UI, so a fading stack let the whole video show through the lower rail for the first frames
        // after the style picker shut (owner video 2026-11-11).
        var anim = Context.Anim;
        _lift ??= node => anim?.SeedValue(node, AnimChannel.Opacity, 0f, MotionTok.StandardEnter, from: 1f);
        var p = UseProps<ClassicBleedProps>();
        var np = p.NowPlaying;
        float w = MathF.Max(1f, p.Width);
        float vh = MathF.Round(w * ClassicHeroLayout.BleedAspect), ph = np.RailHeight.Value;
        // Where the picture has fully given way to the ground: the meta starts BleedMetaDrop under it (ClassicNowPlayingHero).
        float end = ClassicHeroLayout.BleedEnd(vh, ph);
        float fade = MathF.Min(end, ClassicHeroLayout.BleedFadeH), half = MathF.Round(0.5f * fade);
        ColorF tint = ClassicBleedInk.Tint(UseContext(ClassicMaterial.StateSlot), UseContext(OptionStore.Context));
        var ink = ClassicBleedInk.Of(np.NowArtUrl(), tint);
        ColorF ground = ink.Ground, shade = Deck.BleedShade(ground);
        // What the fade's halves paint back over the picture's hole: the panel's own layers (translucent), on the complementary curve.
        ColorF coat = ColorContrast.Over(R.Layer.FileArea, tint);

        // The gradients' cross-fade (GroundFade). A new tint starts it from the colour shown; a light/dark switch does not (the
        // engine's theme fade carries a gradient then). The change render draws the start statically (mix 0); the render after it
        // binds the mix (a fresh bind evaluates at once, so no frame shows a stale mix with new colours), and the bind reads the frame
        // tick only while the fade runs. A timeout at the fade's end re-renders it static on the new tint: nothing ticks at rest.
        bool light = R.IsLight;
        var gfRef = UseRef<GroundFade?>(null);
        var gf = gfRef.Value ??= new GroundFade { From = coat, To = coat, Light = light };
        var tick = UseContextSignal(FrameClock.Tick);
        var kick = UseSignal(0);
        var settled = UseSignal(0);
        int kicked = kick.Value, done = settled.Value;
        if (gf.Fading && done == gf.Epoch) gf.Fading = false;
        if (coat != gf.To)
        {
            bool cross = light == gf.Light;
            gf.From = cross ? gf.Shown : coat;
            gf.To = coat;
            gf.StartQpc = FrameClock.FrameQpc;
            gf.Epoch++;
            gf.Fading = cross;
        }
        gf.Light = light;
        gf.MixNow ??= () => { if (gf.Fading && gf.Mix() < 1f) _ = tick.Value; return gf.Mix(); };
        gf.Settle ??= () => { if (gf.Mix() < 1f) gf.Done.Restart(); else settled.Value = gf.Epoch; };
        int epoch = gf.Epoch;
        UseLayoutEffect(() => { if (kick.Peek() != epoch) kick.Value = epoch; }, DepKey.From(epoch));
        gf.Done = UseTimeout(gf.Settle, Design.Motion.Standard, DepKey.From(epoch));
        GradientSpec? to0 = null, to1 = null;
        Prop<float> mix = 0f;
        if (gf.Fading)
        {
            to0 = HalfSpec(coat, ClassicHeroLayout.BleedFadeA0, ClassicHeroLayout.BleedFadeA1, ClassicHeroLayout.BleedFadeA2, ClassicHeroLayout.BleedFadeA3);
            to1 = HalfSpec(coat, ClassicHeroLayout.BleedFadeA3, ClassicHeroLayout.BleedFadeA4, ClassicHeroLayout.BleedFadeA5, ClassicHeroLayout.BleedFadeA6);
            if (kicked == epoch) mix = Prop.Of(gf.MixNow);
        }
        ColorF from = gf.Fading ? gf.From : coat;
        float cut = MathF.Max(0f, end), top = MathF.Max(0f, end - fade), span = MathF.Max(1f, fade), lowerH = fade - half;
        // The picture's ramp: the gradients' stops at the thirds of their halves, the picture's alpha the complement of the coat's.
        Span<VideoAlphaStop> stops =
        [
            Stop(0f, ClassicHeroLayout.BleedFadeA0, span),
            Stop(half * ClassicHeroLayout.BleedFadeThird, ClassicHeroLayout.BleedFadeA1, span),
            Stop(half * ClassicHeroLayout.BleedFadeTwoThirds, ClassicHeroLayout.BleedFadeA2, span),
            Stop(half, ClassicHeroLayout.BleedFadeA3, span),
            Stop(half + lowerH * ClassicHeroLayout.BleedFadeThird, ClassicHeroLayout.BleedFadeA4, span),
            Stop(half + lowerH * ClassicHeroLayout.BleedFadeTwoThirds, ClassicHeroLayout.BleedFadeA5, span),
            Stop(fade, ClassicHeroLayout.BleedFadeA6, span),
        ];
        var ramp = VideoAlphaRamp.Create(VideoRampAxis.TopToBottom, top, top + span, stops);
        var rampSig = UseSignal(ramp);
        UseLayoutEffect(() => { if (!rampSig.Peek().Equals(ramp)) rampSig.Value = ramp; }, DepKey.From(ramp.GetHashCode()));
        float veilTop = cut > 0f ? top / cut : 0f;
        return Flow.Show(np.Bleeds, new BoxEl
        {
            // Shrink, no minimum and a clip: a canvas taller than the panel must not grow the panel's stack (it is cut at the panel's foot).
            ZStack = true, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, MinHeight = 0f, ClipToBounds = true, HitTestVisible = false,
            Exit = Fade, Transition = MotionTok.StandardEnter,
            Children =
            [
                // The canvas, cut where its picture has gone: its hole stops there, so the panel below is never erased. The provider is the
                // box's child (a provider placed as the ZStack's own child hugs its content: the canvas measured 56 DIP wide).
                new BoxEl
                {
                    AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Stretch, Height = cut, ZStack = true, ClipToBounds = true, HitTestVisible = false,
                    Children = [Ctx.Provide(CanvasVideo.Ramp, rampSig, np.BleedVideo(w))],
                },
                // The dissolve: the coat's alpha rises on an eased curve (slow off the picture, slow into the ground) as the picture's
                // falls, so neither end of the fade shows an edge. Two gradients, the curve's two halves (a gradient holds four stops).
                Half(end - fade, half, HalfSpec(from, ClassicHeroLayout.BleedFadeA0, ClassicHeroLayout.BleedFadeA1, ClassicHeroLayout.BleedFadeA2, ClassicHeroLayout.BleedFadeA3), to0, mix),
                Half(end - fade + half, fade - half, HalfSpec(from, ClassicHeroLayout.BleedFadeA3, ClassicHeroLayout.BleedFadeA4, ClassicHeroLayout.BleedFadeA5, ClassicHeroLayout.BleedFadeA6), to1, mix),
                new BoxEl
                {
                    AlignSelf = FlexAlign.Start, Height = ClassicHeroLayout.BleedHeadScrimH,
                    Gradient = Ui.GradientDown(new GradientStop(0f, shade with { A = ClassicHeroLayout.BleedHeadScrimA }), new GradientStop(1f, shade with { A = 0f })),
                },
                // The entrance veil: the stand-in over the picture, dissolving into the panel over the fade, lifted by the seed above.
                new BoxEl
                {
                    Key = VeilKey, AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Stretch, Height = cut, Opacity = 0f, HitTestVisible = false, OnRealized = _lift,
                    Gradient = Ui.GradientDown(new GradientStop(0f, ground), new GradientStop(veilTop, ground), new GradientStop(1f, ground with { A = 0f })),
                },
            ],
        });
    }

    /// <summary>A ramp stop <paramref name="at"/> DIP into a fade <paramref name="span"/> long, where the coat's alpha is
    /// <paramref name="coatA"/>: the picture's is its complement.</summary>
    static VideoAlphaStop Stop(float at, float coatA, float span) => new(at / span, 1f - coatA);

    /// <summary>One half of the fade: <paramref name="h"/> tall from <paramref name="top"/>, blending from <paramref name="g"/> toward
    /// <paramref name="to"/> by <paramref name="mix"/> while a page's tint cross-fades.</summary>
    static Element Half(float top, float h, GradientSpec g, GradientSpec? to, Prop<float> mix) => new BoxEl
    {
        AlignSelf = FlexAlign.Start, Margin = new Edges4(0f, top, 0f, 0f), Height = h, HitTestVisible = false,
        Gradient = g, GradientTo = to, GradientMix = mix,
    };

    /// <summary>A half's gradient: <paramref name="coat"/> at four alphas a third apart, scaled by its own (the translucent panel layers reach
    /// their own alpha where the picture has gone).</summary>
    static GradientSpec HalfSpec(ColorF coat, float a0, float a1, float a2, float a3) => Ui.GradientDown(
        new GradientStop(0f, coat with { A = coat.A * a0 }), new GradientStop(ClassicHeroLayout.BleedFadeThird, coat with { A = coat.A * a1 }),
        new GradientStop(ClassicHeroLayout.BleedFadeTwoThirds, coat with { A = coat.A * a2 }), new GradientStop(1f, coat with { A = coat.A * a3 }));
}
