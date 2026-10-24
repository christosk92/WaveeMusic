// ── Themes/Classic/Structure/Rules/ClassicLikedRules.cs — the numbers the Liked Songs views own (themes plan 6.1, T9c) ──────────
//
// From `Entities\User.Cover.cs`: the plates and washes of the generated cover (imagery constants: a cover treatment is artwork, and
// artwork has no theme) and the sizes of the chrome drawn over it. WVTH003 allows literal looks only here. The INKS over the artwork
// (the name chip's word and glyph, the rim and sheen, the count) are not here: they are the on-media ladder (`R.OnMedia`, P21).

using FluentGpu.Foundation;

namespace Wavee.Themes.Classic;

/// <summary>Classic's Liked Songs numbers.</summary>
internal static class ClassicLikedRules
{
    // ── the plates and the badge (imagery constants) ──

    /// <summary>The plate the two full-bleed grids sit on (#131318).</summary>
    public static readonly ColorF PlateDark = ColorF.FromRgba(0x13, 0x13, 0x18);

    /// <summary>The wall's two-layer vignette ink.</summary>
    public static readonly ColorF VignetteInk = ColorF.FromRgba(0x0A, 0x0A, 0x0E);

    /// <summary>Marquee's ground plate before the palette tints it.</summary>
    public static readonly ColorF MarqueeBase = ColorF.FromRgba(0x17, 0x17, 0x1C);

    /// <summary>Stack's ground plate before the palette tints it.</summary>
    public static readonly ColorF StackBase = ColorF.FromRgba(0x1B, 0x1B, 0x20);

    /// <summary>The white badge disc: a constant, because it sits on a palette GROUND and tinting it from the same swatch is how it would vanish.</summary>
    public static readonly ColorF BadgeDisc = ColorF.FromRgba(255, 255, 255);

    /// <summary>The heart's glyph on the badge disc: near-black, a constant for the same reason.</summary>
    public static readonly ColorF BadgeGlyph = ColorF.FromRgba(0x1B, 0x1B, 0x20);

    /// <summary>The badge's cast on ART: black at 0x59, fixed (a theme-split token is a 10 % whisper in light theme that a white disc on a
    /// bright sleeve loses).</summary>
    public static readonly ColorF BadgeCast = ColorF.FromRgba(0, 0, 0, 0x59);

    // ── the name chip (bottom-left over the art) ──

    public const float NameChipGap = 8f;
    public const float NameChipHeight = 34f;

    // ── the heart badge ──

    public const float BadgeEdge = 40f;
    public const float BadgeGlyphEdge = 20f;

    // ── the Tone count (the collection size, bottom-right) ──

    public const float ToneCountSize = 12f;
    public const float ToneCountLine = 16f;

    // ── the picker ──

    /// <summary>The "Cover style" pill's inset from the cover's bottom-right corner.</summary>
    public static readonly Edges4 PillMargin = new(0f, 0f, 10f, 10f);

    /// <summary>The flyout's card padding (the WinUI FlyoutContentPadding).</summary>
    public static readonly Edges4 FlyoutPad = new(16f, 15f, 16f, 17f);

    // ── the inline chips (the page bar, tuned Classic D8) ──

    /// <summary>The fade over the chips' trailing (and, once scrolled, leading) edge (board C-Liked: a 24-px mask).</summary>
    public const float InlineChipFade = 24f;

    // ── the flyout's note ──

    public const float NoteSize = 12f;
    public const float NoteLine = 16f;
}
