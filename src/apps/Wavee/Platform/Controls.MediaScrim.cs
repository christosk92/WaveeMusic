// ── Platform/Controls.MediaScrim.cs — the adaptive scrim boost under text on a photograph (MediaScrim's view half) ─────────────────────
//
// Role: UI (core; every theme mounts it over its own photograph and floor veil)
//
// A surface that sets on-media copy over a photograph lays this layer over its own static veil (the floor), between the veil and the copy:
// a black gradient, clear at the box's top and full from `CopyTop` down, at the opacity `MediaScrim.SolveBoost` finds for this photograph
// (`Palette.Tone`) and this box. Until the photograph is measured the opacity is 0 (the floor alone, the surface's board look), and every
// change of the answer (the measurement landing, a resize, a tier crossing) FADES over `MediaScrim.FadeMs`: the veil never jumps. It is
// paint only (`HitTestVisible = false`, no layout of its own beyond the box it fills).

using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

public static partial class Controls
{
    /// <summary>What the boost reads: the photograph, the box it fills, the copy's lines, the surface's floor veil, the ceiling, where the
    /// copy starts (box DIP; the gradient is full from there down) and how the surface crops the photograph (<c>default</c>: centred).</summary>
    public sealed record ScrimBoostProps(string? Url, float Width, float Height, MediaScrim.Band[] Bands, MediaScrim.Floor Floor, float Ceiling, float CopyTop,
        MediaScrim.Crop Crop = default)
    {
        public bool Equals(ScrimBoostProps? o) => o is not null && Url == o.Url && Width == o.Width && Height == o.Height && Floor == o.Floor
            && Ceiling == o.Ceiling && CopyTop == o.CopyTop && Crop == o.Crop && Bands.AsSpan().SequenceEqual(o.Bands);

        public override int GetHashCode() => HashCode.Combine(Url, Width, Height, Floor, Ceiling, CopyTop, Crop, Bands.Length);
    }

    /// <summary>The adaptive boost over a photograph's floor veil (see the file header). Place it as a ZStack layer the size of the box, after the
    /// veil and before the copy.</summary>
    public static Element ScrimBoost(ScrimBoostProps props) => Embed.Comp(props, static () => new ScrimBoostHost());

    /// <summary>The boost's gradient stop where the ramp is half way to the copy, and its alpha there (an ease-in, so the photograph's top stays clear).</summary>
    const float BoostMidAlpha = 0.3f;

    sealed class ScrimBoostHost : Component
    {
        public override Element Render()
        {
            ScrimBoostProps p = UseProps<ScrimBoostProps>();
            MediaScrim.LumaMap? map = Palette.Tone(p.Url);
            float target = map is null ? 0f : MediaScrim.SolveBoost(map, p.Width, p.Height, p.Bands, p.Floor, R.OnMedia.Ink, p.Ceiling, p.Crop);
            Ref<float> shown = UseRef(target);
            float from = shown.Value;
            shown.Value = target;
            UseTransition(AnimChannel.Opacity, from, target, Motion.ReducedMotion ? 0f : MediaScrim.FadeMs, Easing.SmoothOut, DepKey.From(target));
            float copy = Math.Clamp(p.CopyTop / MathF.Max(1f, p.Height), 0.02f, 1f);
            ColorF black = ColorF.FromRgba(0, 0, 0);
            return new BoxEl
            {
                Width = p.Width, Height = p.Height, HitTestVisible = false, Opacity = target,
                Gradient = GradientDown(new GradientStop(0f, black with { A = 0f }), new GradientStop(copy / 2f, black with { A = BoostMidAlpha }),
                    new GradientStop(copy, black), new GradientStop(1f, black)),
            };
        }
    }
}
