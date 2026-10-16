// ── Themes/Classic/Structure/Rules/ClassicVideoRules.cs — the numbers of Classic's video surfaces (themes plan 6.1, T7c) ──────────
//
// What `Shell/Video.UI.cs` and `Shell/Rail.UI.cs` spelled beside the pictures, moved verbatim with them: the poster's dimming and its
// notice (the 20-DIP ring, the 12-DIP words, the 18-DIP warning glyph), the 24-DIP hover glyph and its 11-DIP icon, the strips' heights (the
// docked card's and the mini player's 30, the fullscreen band's 56, the pop-out band's insets), the fullscreen exit plate (44, its 18-DIP
// glyph), the resize zones' one painted corner mark, the watch page's play disc and the "no video for this song" placeholder. They are data,
// not decisions: the geometry the core's gestures and the engine's overlay run on (`Video.Pip.CornerW`, `EdgeBand`, the pop-out band's height
// `Video.PopOutBand.HeightDip`, the caption region's input) stays core and Classic reads it.
// WVTH003 allows literals only here, in `Rules\**`.

namespace Wavee.Themes.Classic;

/// <summary>The constants of Classic's video surfaces.</summary>
public static class ClassicVideoRules
{
    // ── the poster and its notice ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The artwork's dimming over the letterbox (the "no picture yet" ground).</summary>
    public const float PosterOpacity = 0.4f;

    /// <summary>The poster artwork's decode size (a ground behind a notice, not a cover).</summary>
    public const float PosterDecodePx = 256f;

    /// <summary>The notice's ring, its words and the failure's glyph.</summary>
    public const float NoticeRing = 20f, NoticeText = 12f, NoticeGlyph = 18f;

    // ── the hover glyph ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A hover-revealed chrome glyph's icon (the tooltip names the DESTINATION). Its box and the strip it sits on are the window
    /// tier's (`ClassicStageTiers.Panel`: 24 on a 30 strip, 40 on a 40 strip at Compact).</summary>
    public const float GlyphSize = 11f;

    /// <summary>The glyph plate's hover and pressed alpha over the on-media ink.</summary>
    public const float GlyphHoverAlpha = 0.14f, GlyphPressedAlpha = 0.22f;

    // ── the strips ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The fullscreen band's height and its inset (the title's left edge adds a small step; the exit plate sits at the right).</summary>
    public const float FullscreenBandH = 56f, BandInset = 12f, FullscreenBandTop = 12f;

    /// <summary>The title's size (the fullscreen band and the pop-out band).</summary>
    public const float TitleSize = 15f;

    /// <summary>The fullscreen exit plate and its glyph.</summary>
    public const float ExitPlate = 44f, ExitGlyph = 18f;

    // ── the mini player's resize zones ───────────────────────────────────────────────────────────────────────────────

    /// <summary>The south-east zone's one painted pixel: the mark's edge and its inset from the corner.</summary>
    public const float GripMark = 8f, GripMarkInset = 2f;

    // ── the watch page's in-page stage ───────────────────────────────────────────────────────────────────────────────

    /// <summary>The idle play disc.</summary>
    public const float PlayDisc = 64f;

    // ── the video panel body's placeholder ───────────────────────────────────────────────────────────────────────────

    /// <summary>"No video for this song" over the dimmed cover: the words' size, and the card's 16:9.</summary>
    public const float NoVideoText = 12f, NoVideoAspect = 16f / 9f;
}
