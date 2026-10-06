// ── Shell/Stage.Layouts.UI.cs ──────────────────────────────────────────────────────────────────────────────────────────
// SpectrumLayer — the strip / ring the visualizer LAYOUTS draw (Large art, Centered, Artist), mounted by SurfaceCore
//
// Role: UI
// Owner: K
// Wave: 7
// Budget: 120 lines
// Spec: Stage.Layouts.cs (the geometry), Visualizer.Strip.UI.cs (the drawing)
//
// ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// A component on the context's SIGNALS (Layout, Look): it re-renders when the layout, the spectrum style or the stage's size
// moves — never per tick. The strip itself is bound to the slab and lives in its own small RepaintBoundary, so a step re-rasters
// only that rect. Nothing is mounted for Card (the face is the picture) or Spectrum Off; the box is keyed by (layout, style, 8-DIP
// width bucket), so a style switch fades the old one out under the new and a drag does not rebuild it on every pixel.

using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Hooks;
using FluentGpu.Foundation;

namespace Wavee;

public static partial class Stage
{
    sealed class SpectrumLayer : Component
    {
        public override Element Render()
        {
            var ctx = UseContext(StageContext)!;
            var L = ctx.Layout.Value;
            var k = ctx.Look.Value;
            var slab = ctx.Slab;
            var fade = new EnterExit(Opacity: 0f, Active: true);
            Element? body = null;
            float x = 0f, y = 0f, w = 0f, h = 0f;
            string key = "";
            if (k.IsLayout)
            {
                switch (k.Spectrum)
                {
                    case SpectrumStyle.Bars:
                    case SpectrumStyle.Line:
                    {
                        (x, y, w, h) = L.StripRect(in k);
                        if (h <= 0f || w <= 0f) break;
                        body = k.Spectrum == SpectrumStyle.Bars ? Visualizer.StripBars(slab, w, h) : Visualizer.StripLine(slab, w, h, k.HeroLyrics);
                        key = "strip:" + (int)k.Eff + ":" + (int)k.Spectrum + ":" + (k.HeroLyrics ? "l" : "a") + ":" + ((int)w >> 3);
                        break;
                    }
                    case SpectrumStyle.Ring:
                    {
                        var box = L.RingBox(in k);
                        if (box.Size <= 0f) break;
                        float cover = box.Size / Layout.RingBoxRatio;
                        (x, y, w, h) = (box.X, box.Y, box.Size, box.Size);
                        body = Visualizer.RingAround(slab, cover);
                        key = "ring:" + (int)k.Eff + ":" + ((int)box.Size >> 2);
                        break;
                    }
                }
            }
            return Layer with
            {
                HitTestVisible = false, HitTestPassThrough = false,
                Children = body is null ? [] :
                [
                    new BoxEl
                    {
                        Key = key, Width = w, Height = h, AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, HitTestVisible = false,
                        Margin = new Edges4(x, y, 0f, 0f), Enter = fade, Exit = fade, Transition = MotionTok.StandardEnter,
                        Children = [body],
                    },
                ],
            };
        }
    }
}
