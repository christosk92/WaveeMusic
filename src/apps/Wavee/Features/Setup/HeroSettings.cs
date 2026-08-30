using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using static Wavee.HeroMotion;

namespace Wavee;

/// <summary>
/// Hero 4 · <see cref="SetupPage.Appearance"/> (the prototype's <c>ob-settings</c>) — a window frame whose theme
/// preview clip-wipes across (a literal <see cref="AnimChannel.ClipL"/>/<see cref="AnimChannel.ClipR"/> reveal, not a
/// translating+masked rect), four palette dots pulsing in sequence, and two slider knobs sliding. Straight
/// lines/rects/circles only — no curve geometry.
/// </summary>
sealed class HeroSettings : Component
{
    static readonly PathData HeaderRule = Geo("M30 62 H162");
    static readonly PathData SliderRail1 = Geo("M52 118 H140");
    static readonly PathData SliderRail2 = Geo("M52 132 H140");

    public override Element Render()
    {
        ColorF accent = Tok.AccentDefault;
        ColorF soft = Tok.TextTertiary;

        var wipe = UseRef<NodeHandle>(default);
        var dot0 = UseRef<NodeHandle>(default);
        var dot1 = UseRef<NodeHandle>(default);
        var dot2 = UseRef<NodeHandle>(default);
        var dot3 = UseRef<NodeHandle>(default);
        var knob1 = UseRef<NodeHandle>(default);
        var knob2 = UseRef<NodeHandle>(default);

        UseLayoutEffect(() =>
        {
            if (Context.Anim is not { } anim || Context.Scene is not { } scene) return;

            // ob-wipe, reimagined on real clip channels: reveal the highlight left-to-right (ClipR 0->W), hold full,
            // then hide it left-to-right (ClipL 0->W) — a literal clip-wipe, not a translating masked rect.
            if (wipe.Value is { IsNull: false } wn && scene.IsLive(wn))
            {
                anim.KeyframesMotion(wn, AnimChannel.ClipR, [K(0, 0f), K(8, 0f), K(46, 132f), K(100, 132f)], LoopMs, ReducedMotionPolicy.KeepFade);
                anim.KeyframesMotion(wn, AnimChannel.ClipL, [K(0, 0f), K(58, 0f), K(96, 132f), K(100, 132f)], LoopMs, ReducedMotionPolicy.KeepFade);
            }

            // ob-swatch-on (staggered .3/.6/.9s): 0%,72%{scale1,opacity.35} 12%,26%{scale1.5,opacity1}.
            Keyframe[] sc = [K(0, 1f), K(12, 1.5f), K(100, 1.5f)];
            Keyframe[] op = [K(0, 0.35f), K(12, 1f), K(100, 1f)];
            void Dot(NodeHandle n, float delayMs)
            {
                if (n.IsNull || !scene.IsLive(n)) return;
                anim.KeyframesMotion(n, AnimChannel.ScaleX, sc, LoopMs, ReducedMotionPolicy.KeepFade, delayMs: delayMs);
                anim.KeyframesMotion(n, AnimChannel.ScaleY, sc, LoopMs, ReducedMotionPolicy.KeepFade, delayMs: delayMs);
                anim.KeyframesMotion(n, AnimChannel.Opacity, op, LoopMs, ReducedMotionPolicy.KeepFade, delayMs: delayMs);
            }
            Dot(dot0.Value, 0f);
            Dot(dot1.Value, 300f);
            Dot(dot2.Value, 600f);
            Dot(dot3.Value, 900f);

            // ob-slide: 0%,100%{translateX 0} 50%{translateX 22} — knob2 delayed .5s.
            Keyframe[] slide = [K(0, 0f), K(50, 22f), K(100, 22f)];
            if (knob1.Value is { IsNull: false } k1 && scene.IsLive(k1))
                anim.KeyframesMotion(k1, AnimChannel.TranslateX, slide, LoopMs, ReducedMotionPolicy.KeepFade);
            if (knob2.Value is { IsNull: false } k2 && scene.IsLive(k2))
                anim.KeyframesMotion(k2, AnimChannel.TranslateX, slide, LoopMs, ReducedMotionPolicy.KeepFade, delayMs: 500f);
        });

        return new BoxEl
        {
            ZStack = true, Width = 192f, Height = 192f,
            Children =
            [
                StrokeRectBox(30, 42, 132, 104, 10, accent, 2.4f),
                new BoxEl
                {
                    Width = 132, Height = 104, OffsetX = 30, OffsetY = 42,
                    OnRealized = n => wipe.Value = n,
                    Children = [ new BoxEl { Width = 132, Height = 104, Fill = accent with { A = accent.A * 0.22f } } ],
                },
                StrokePath(HeaderRule, soft, LineStroke(2f)),
                DiscBox(56, 94, 12, accent, n => dot0.Value = n),
                DiscBox(80, 94, 12, accent, n => dot1.Value = n),
                DiscBox(104, 94, 12, accent, n => dot2.Value = n),
                DiscBox(128, 94, 12, accent, n => dot3.Value = n),
                StrokePath(SliderRail1, soft, LineStroke(2f)),
                StrokePath(SliderRail2, soft, LineStroke(2f)),
                DiscBox(70, 118, 10, accent, n => knob1.Value = n),
                DiscBox(98, 132, 10, accent, n => knob2.Value = n),
            ],
        };
    }
}
