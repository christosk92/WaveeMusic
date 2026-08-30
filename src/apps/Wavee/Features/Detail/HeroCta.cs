using System;
using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee;

/// <summary>Shared prominent hero CTA skin. Artist and collection detail surfaces use this single primitive so their
/// Play/Shuffle geometry, motion, typography, and palette transitions cannot drift independently.</summary>
static class HeroCta
{
    // The WaveeCta media pill: stock Button internals (focus ring, automation role, 83ms brush ramp) wearing the media
    // capsule (Radii.Full at 36px, bold label, hover/press scale, hand cursor). `fill`/`foreground` remain the caller's
    // extracted accent + its resolved ink.
    // Signature is load-bearing: both the artist hero and the collection rail route through it, so they move together.
    public static Element Pill(string glyph, string label, ColorF fill, ColorF foreground, Action onClick,
                               bool balanced = false)
    {
        var pill = WaveeCta.Accent(label, fill, onClick, glyph, foreground);
        return balanced
            ? pill with { Grow = 1f, Basis = 0f, MinWidth = 0f, MaxWidth = 200f }
            : pill;
    }
}
