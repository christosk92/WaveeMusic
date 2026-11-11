// ── Themes/Classic/Structure/Views/Panel/ClassicBleed.cs — Classic's edge-to-edge canvas ───────────────────────────────────────────────
//
// The Canvas player style at rest fills the panel from its very top (`NowPlayingHeroProps.BleedVideo`, core) and dissolves into the panel:
// over its last stretch the picture fades, on an eased curve, into the panel's own plain ground, the same ground the panel shows with the
// canvas off (the owner, 2026-11-08: "theres too much color. it should just be plain"). Nothing moves there and nothing is tinted by the art
// (the owner, 2026-11-10: "get rid of the wave effect, just fade it beautifully"): the art lives in the picture and in the meta's accent text.
//
// The ground is OPAQUE (the video is a plane under the UI and would show through translucent chrome), so it cannot let the page's shell tint
// through the way the translucent rail does: it paints it instead. The panel with the canvas off is the content layer over the material's
// tint over the window's backdrop; the bleed flattens exactly that (`ClassicMaterial.TintFill`, the layer's own rule, over the backdrop's
// opaque stand-in `Tok.WindowBackground`, what the window clears to without Mica and what a capture composes under it), so the plain ground
// keeps the page's colour like the player bar beside it (the owner, 2026-11-11: "the fade here with the canvas is just black/dark. no color").
// Not matched: live Mica's wallpaper colour, and Home's radial washes (one opaque colour cannot carry a gradient); there the ground is the
// page's flat tint (or the neutral ground) over that stand-in.

using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>The colours of the canvas's ending and its meta: the plain panel <paramref name="Ground"/> the picture fades into, and the meta's
/// accent text (<paramref name="Text"/>: the artist, the heart).</summary>
internal readonly record struct ClassicBleedInk(ColorF Ground, ColorF Text)
{
    /// <summary>The material's colour under the panel: the presented state's tint as the material layer paints it
    /// (<see cref="ClassicMaterial.TintFill"/>), or the neutral ground with no frame. Tracked reads (the state at navigation rate, the
    /// Surfaces option): the caller re-renders when the page's tint or the option changes, never per frame.</summary>
    public static ColorF Tint(IReadSignal<ShellMaterialState>? state, IOptionReader? options)
        => state is null ? R.Wash.NeutralGround : ClassicMaterial.TintFill(state.Value, ClassicMaterial.Level(options));

    /// <summary>The colours for the cover at <paramref name="url"/> over the material <paramref name="tint"/> (<see cref="Tint"/>). The ground
    /// is the panel's own, flattened: the content layer over the tint over the window's backdrop (its opaque stand-in), what the docked
    /// panel shows with the canvas off. The accent text is the cover's dominant hue (its grading, the house palette) solved as text on that ground; a cover with no
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
    /// <summary>The stack's plates and its entrance veil (inspection reads the plates' opacity on the frames after the picker shuts).</summary>
    public const string GroundKey = "classic-bleed-ground", FootKey = "classic-bleed-foot", VeilKey = "classic-bleed-veil";

    static readonly EnterExit Fade = new(Opacity: 0f, Active: true);
    Action<NodeHandle>? _lift;

    /// <summary>The fade gradients' ground cross-fade. The plates fade a page's new ground on their brush transition (linear over
    /// <see cref="Design.Motion.Standard"/>, from the colour they show); a gradient has no brush channel, so the two halves carry the old
    /// ground as <c>Gradient</c>, the new one as <c>GradientTo</c>, and a <c>GradientMix</c> on the frame clock walks the same line in
    /// step (an opacity cross-fade of two gradient layers over the video dips their alpha and lets the picture through: never exact).</summary>
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
        // The entrance is a veil of the ground lifting off the stack (1 -> 0 on the entrance token), never a fade of the stack itself: the
        // window is live Mica, the rail translucent and the video a plane UNDER the UI, so a fading stack let the video show through the
        // lower rail (where the opaque plates hide it at rest) for the first frames after the style picker shut (owner video 2026-11-11).
        // Over the opaque ground the veil composes the same dissolve the stack's fade drew; the plates hold from the first frame.
        var anim = Context.Anim;
        _lift ??= node => anim?.SeedValue(node, AnimChannel.Opacity, 0f, MotionTok.StandardEnter, from: 1f);
        var p = UseProps<ClassicBleedProps>();
        var np = p.NowPlaying;
        float w = MathF.Max(1f, p.Width);
        float vh = MathF.Round(w * ClassicHeroLayout.BleedAspect), ph = np.RailHeight.Value;
        // Where the picture has fully given way to the ground: the meta starts BleedMetaDrop under it (ClassicNowPlayingHero).
        float end = ClassicHeroLayout.BleedEnd(vh, ph);
        float fade = MathF.Min(end, ClassicHeroLayout.BleedFadeH), half = MathF.Round(0.5f * fade);
        var ink = ClassicBleedInk.Of(np.NowArtUrl(), ClassicBleedInk.Tint(UseContext(ClassicMaterial.StateSlot), UseContext(OptionStore.Context)));
        ColorF ground = ink.Ground, shade = Deck.BleedShade(ground);

        // The gradients' cross-fade (GroundFade). A new ground starts it from the colour shown; a light/dark switch does not (the
        // engine's theme fade carries a gradient then). The change render draws the start statically (mix 0); the render after it
        // binds the mix (a fresh bind evaluates at once, so no frame shows a stale mix with new colours), and the bind reads the frame
        // tick only while the fade runs. A timeout at the fade's end re-renders it static on the new ground: nothing ticks at rest.
        bool light = R.IsLight;
        var gfRef = UseRef<GroundFade?>(null);
        var gf = gfRef.Value ??= new GroundFade { From = ground, To = ground, Light = light };
        var tick = UseContextSignal(FrameClock.Tick);
        var kick = UseSignal(0);
        var settled = UseSignal(0);
        int kicked = kick.Value, done = settled.Value;
        if (gf.Fading && done == gf.Epoch) gf.Fading = false;
        if (ground != gf.To)
        {
            bool cross = light == gf.Light;
            gf.From = cross ? gf.Shown : ground;
            gf.To = ground;
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
            to0 = HalfSpec(ground, ClassicHeroLayout.BleedFadeA0, ClassicHeroLayout.BleedFadeA1, ClassicHeroLayout.BleedFadeA2, ClassicHeroLayout.BleedFadeA3);
            to1 = HalfSpec(ground, ClassicHeroLayout.BleedFadeA3, ClassicHeroLayout.BleedFadeA4, ClassicHeroLayout.BleedFadeA5, ClassicHeroLayout.BleedFadeA6);
            if (kicked == epoch) mix = Prop.Of(gf.MixNow);
        }
        ColorF from = gf.Fading ? gf.From : ground;
        return Flow.Show(np.Bleeds, new BoxEl
        {
            // Shrink, no minimum and a clip: a canvas taller than the panel must not grow the panel's stack (it is cut at the panel's foot).
            ZStack = true, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, MinHeight = 0f, ClipToBounds = true, HitTestVisible = false,
            Exit = Fade, Transition = MotionTok.StandardEnter,
            Children =
            [
                // The plates cross-fade a page's new tint with the material layer beside them (their brush transition); the two fade
                // gradients below follow them in step on their own mix (GroundFade: a gradient has no brush-fade channel).
                new BoxEl { Key = GroundKey, Grow = 1f, Fill = ground, BrushTransitionMs = Design.Motion.Standard },
                np.BleedVideo(w),
                // The dissolve: the ground's alpha rises on an eased curve (slow off the picture, slow into the ground), so neither end
                // of the fade shows an edge. Two gradients, the curve's two halves (a gradient holds four stops).
                Half(end - fade, half, HalfSpec(from, ClassicHeroLayout.BleedFadeA0, ClassicHeroLayout.BleedFadeA1, ClassicHeroLayout.BleedFadeA2, ClassicHeroLayout.BleedFadeA3), to0, mix),
                Half(end - fade + half, fade - half, HalfSpec(from, ClassicHeroLayout.BleedFadeA3, ClassicHeroLayout.BleedFadeA4, ClassicHeroLayout.BleedFadeA5, ClassicHeroLayout.BleedFadeA6), to1, mix),
                // Under the fade the plain ground covers the canvas's last stretch (and the panel below it), as the panel is with the canvas off.
                new BoxEl { Key = FootKey, AlignSelf = FlexAlign.Stretch, Margin = new Edges4(0f, end, 0f, 0f), Fill = ground, BrushTransitionMs = Design.Motion.Standard },
                new BoxEl
                {
                    AlignSelf = FlexAlign.Start, Height = ClassicHeroLayout.BleedHeadScrimH,
                    Gradient = Ui.GradientDown(new GradientStop(0f, shade with { A = ClassicHeroLayout.BleedHeadScrimA }), new GradientStop(1f, shade with { A = 0f })),
                },
                // The entrance veil: opaque ground on the first frame, lifted by the seed above; at rest it draws nothing.
                new BoxEl { Key = VeilKey, Fill = ground, Opacity = 0f, HitTestVisible = false, OnRealized = _lift },
            ],
        });
    }

    /// <summary>One half of the fade: <paramref name="h"/> tall from <paramref name="top"/>, blending from <paramref name="g"/> toward
    /// <paramref name="to"/> by <paramref name="mix"/> while a page's ground cross-fades.</summary>
    static Element Half(float top, float h, GradientSpec g, GradientSpec? to, Prop<float> mix) => new BoxEl
    {
        AlignSelf = FlexAlign.Start, Margin = new Edges4(0f, top, 0f, 0f), Height = h, HitTestVisible = false,
        Gradient = g, GradientTo = to, GradientMix = mix,
    };

    /// <summary>A half's gradient: the ground at four alphas a third apart.</summary>
    static GradientSpec HalfSpec(ColorF ground, float a0, float a1, float a2, float a3) => Ui.GradientDown(
        new GradientStop(0f, ground with { A = a0 }), new GradientStop(ClassicHeroLayout.BleedFadeThird, ground with { A = a1 }),
        new GradientStop(ClassicHeroLayout.BleedFadeTwoThirds, ground with { A = a2 }), new GradientStop(1f, ground with { A = a3 }));
}
