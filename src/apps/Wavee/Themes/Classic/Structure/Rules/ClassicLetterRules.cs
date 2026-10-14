// ── Themes/Classic/Structure/Rules/ClassicLetterRules.cs — the numbers of the letter pictures (themes plan 6.1, T10c2) ──────────────────────────────
//
// What the core held as literals in the letter plate and the A-Z strip, moved with them into `Views\Pages\ClassicLetterKit.cs`
// (WVTH003 allows literals only in `Rules\**`).

namespace Wavee.Themes.Classic;

/// <summary>The constants of the letter plate and the A-Z strip.</summary>
public static class ClassicLetterRules
{
    /// <summary>The plate's glyph: size, line height, letter spacing.</summary>
    public const float PlateSize = 12f, PlateLine = 16f, PlateSpacing = 60f;

    /// <summary>The plate: horizontal and vertical padding, corner radius.</summary>
    public const float PlatePadX = 6f, PlatePadY = 1f, PlateRadius = 4f;

    /// <summary>One strip row: width and height; the strip's own width; the row corner radius; the opacity of an absent letter.</summary>
    public const float StripRowW = 18f, StripRowH = 13f, StripW = 18f, StripRowRadius = 3f, AbsentOpacity = 0.3f;

    /// <summary>The ink weights: the resting run, the active run and the plate's glyph.</summary>
    public const ushort InkWeight = 400, InkActiveWeight = 700, PlateWeight = 600;
}
