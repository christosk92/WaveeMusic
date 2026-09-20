using System;
using FluentGpu.Foundation;

namespace Wavee;

/// <summary>One shell wash resolved from its WINDOW-relative ellipse to the CLIPPED box the shell actually paints, plus
/// that ellipse re-expressed relative to the clipped box (which is the space
/// <c>GradientSpec.RadialCenter</c>/<c>RadialRadius</c> live in).
/// <para><see cref="W"/>/<see cref="H"/> are window fractions; <see cref="AnchorRight"/>/<see cref="AnchorBottom"/> say
/// which window edge the box hangs off (a clamped ellipse always touches at least one), so the layer is placed by ZStack
/// self-alignment and never needs a transform. <see cref="Center"/>/<see cref="Radius"/> are node-relative CONSTANTS: the
/// box is a fraction of the window and the ellipse scales with it, so a resize only re-scales the box.</para></summary>
public readonly record struct ShellWashPlacement(
    float W, float H, bool AnchorRight, bool AnchorBottom, Point2 Center, Point2 Radius, float FadeOffset);

/// <summary>The prototype's three Home washes as pure geometry — the ONE place the window-relative constants live.
/// <para>Clipping is a paint budget, not a look: an unclipped wash is a full-screen blended pass, and three of them cost
/// three. Each layer is sized to the bounding box of its own ellipse AT the transparent stop (outside which it
/// contributes nothing), clamped to the window — so the same pixels are produced from ~a quarter of the fill rate.</para></summary>
public static class ShellWashGeometry
{
    // Window-relative source geometry, in stacking order. Centres/radii are fractions of the window; the fade offset is
    // the gradient stop at which the wash reaches alpha 0.
    public static readonly ShellWashPlacement Hero =
        Resolve(new Point2(0.06f, 0.00f), new Point2(0.74f, 0.92f), 0.62f);
    public static readonly ShellWashPlacement Weekly =
        Resolve(new Point2(0.92f, 0.10f), new Point2(0.58f, 0.78f), 0.64f);
    public static readonly ShellWashPlacement Mix =
        Resolve(new Point2(0.58f, 1.00f), new Point2(0.90f, 0.70f), 0.66f);

    // Alpha at the wash origin (offset 0). Dark carries roughly twice the light strength: the same colour reads far
    // weaker over the dark ground, and the light ground has less headroom before a wash turns into a smudge.
    public const float HeroAlphaLight = 0.055f, ShelfAlphaLight = 0.05f;
    public const float HeroAlphaDark = 0.10f, ShelfAlphaDark = 0.085f;

    public static float HeroAlpha(bool light) => light ? HeroAlphaLight : HeroAlphaDark;
    public static float ShelfAlpha(bool light) => light ? ShelfAlphaLight : ShelfAlphaDark;

    /// <summary>Clip a window-relative radial wash to the bounds of its own ellipse at <paramref name="fadeOffset"/> and
    /// re-express the ellipse relative to that box. Pure — no theme, no viewport, no engine state.</summary>
    public static ShellWashPlacement Resolve(Point2 center, Point2 radius, float fadeOffset)
    {
        float x0 = Math.Clamp(center.X - radius.X * fadeOffset, 0f, 1f);
        float x1 = Math.Clamp(center.X + radius.X * fadeOffset, 0f, 1f);
        float y0 = Math.Clamp(center.Y - radius.Y * fadeOffset, 0f, 1f);
        float y1 = Math.Clamp(center.Y + radius.Y * fadeOffset, 0f, 1f);
        float w = MathF.Max(x1 - x0, 1e-4f), h = MathF.Max(y1 - y0, 1e-4f);
        // A box whose leading edge left the window edge hangs off the TRAILING one (and vice versa); a full-span axis
        // touches both, and leading wins because Start is also the fill-the-slot arm of the ZStack arranger.
        return new ShellWashPlacement(w, h, AnchorRight: x0 > 0f, AnchorBottom: y0 > 0f,
            new Point2((center.X - x0) / w, (center.Y - y0) / h),
            new Point2(radius.X / w, radius.Y / h),
            fadeOffset);
    }
}
