using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using static Wavee.HeroMotion;

namespace Wavee;

/// <summary>
/// Hero 0 · <see cref="SetupPage.Welcome"/> (the prototype's <c>ob-welcome</c>) — the Wavee mark breathing: three
/// concentric rings pulsing outward (staggered 1.1s/2.2s), a core disc breathing, and a 5-bar meter rippling
/// (staggered 0.14s per bar). Pure circles/rects/scale/opacity — no curve geometry needed, so this is BoxEl only,
/// no <see cref="PathEl"/>.
/// </summary>
sealed class HeroWelcome : Component
{
    public override Element Render()
    {
        ColorF accent = Tok.AccentDefault;

        var ring1 = UseRef<NodeHandle>(default);
        var ring2 = UseRef<NodeHandle>(default);
        var ring3 = UseRef<NodeHandle>(default);
        var core = UseRef<NodeHandle>(default);
        var bar0 = UseRef<NodeHandle>(default);
        var bar1 = UseRef<NodeHandle>(default);
        var bar2 = UseRef<NodeHandle>(default);
        var bar3 = UseRef<NodeHandle>(default);
        var bar4 = UseRef<NodeHandle>(default);

        UseLayoutEffect(() =>
        {
            if (Context.Anim is not { } anim || Context.Scene is not { } scene) return;

            // ob-ring: 0%{scale .72,opacity 0} 12%{opacity .55} 70%,100%{scale 1.9,opacity 0} — rg2/rg3 staggered by
            // CSS animation-delay (the engine's per-call delayMs is the direct analogue).
            void Ring(NodeHandle n, float delayMs)
            {
                if (n.IsNull || !scene.IsLive(n)) return;
                anim.KeyframesMotion(n, AnimChannel.ScaleX, [K(0, 0.72f), K(70, 1.9f), K(100, 1.9f)], LoopMs, ReducedMotionPolicy.KeepFade, delayMs: delayMs);
                anim.KeyframesMotion(n, AnimChannel.ScaleY, [K(0, 0.72f), K(70, 1.9f), K(100, 1.9f)], LoopMs, ReducedMotionPolicy.KeepFade, delayMs: delayMs);
                anim.KeyframesMotion(n, AnimChannel.Opacity, [K(0, 0f), K(12, 0.55f), K(70, 0.4f), K(100, 0.4f)], LoopMs, ReducedMotionPolicy.KeepFade, delayMs: delayMs);
            }
            Ring(ring1.Value, 0f);
            Ring(ring2.Value, 1100f);
            Ring(ring3.Value, 2200f);

            // ob-core: 0%,100%{scale 1} 42%{scale 1.06} — the mark's core breathing.
            if (core.Value is { IsNull: false } cn && scene.IsLive(cn))
            {
                anim.KeyframesMotion(cn, AnimChannel.ScaleX, [K(0, 1f), K(42, 1.06f), K(100, 1f)], LoopMs, ReducedMotionPolicy.KeepFade);
                anim.KeyframesMotion(cn, AnimChannel.ScaleY, [K(0, 1f), K(42, 1.06f), K(100, 1f)], LoopMs, ReducedMotionPolicy.KeepFade);
            }

            // ob-bar (1.1s sub-loop, 5 bars staggered 0.14s each): 0%,100%{scaleY .35} 30%{scaleY 1} 60%{scaleY .55} 80%{scaleY .9}.
            const float barMs = 1100f;
            Keyframe[] barKeys = [K(0, 0.35f), K(30, 1f), K(60, 0.55f), K(80, 0.9f), K(100, 1f)];
            var bars = new[] { bar0.Value, bar1.Value, bar2.Value, bar3.Value, bar4.Value };
            for (int i = 0; i < bars.Length; i++)
            {
                var b = bars[i];
                if (b.IsNull || !scene.IsLive(b)) continue;
                anim.KeyframesMotion(b, AnimChannel.ScaleY, barKeys, barMs, ReducedMotionPolicy.KeepFade, delayMs: i * 140f);
            }
        });

        ColorF ringStroke = accent with { A = accent.A * 0.85f };
        return new BoxEl
        {
            ZStack = true, Width = 192f, Height = 192f,
            Children =
            [
                RingBox(96, 96, 68, ringStroke, 2.2f, n => ring1.Value = n),
                RingBox(96, 96, 68, ringStroke, 2.2f, n => ring2.Value = n),
                RingBox(96, 96, 68, ringStroke, 2.2f, n => ring3.Value = n),
                DiscBox(96, 96, 60, accent with { A = accent.A * 0.16f }, n => core.Value = n),
                RectBox(82, 88, 4, 16, 2, accent, n => bar0.Value = n),
                RectBox(89, 84, 4, 24, 2, accent, n => bar1.Value = n),
                RectBox(96, 80, 4, 32, 2, accent, n => bar2.Value = n),
                RectBox(103, 84, 4, 24, 2, accent, n => bar3.Value = n),
                RectBox(110, 88, 4, 16, 2, accent, n => bar4.Value = n),
            ],
        };
    }
}
