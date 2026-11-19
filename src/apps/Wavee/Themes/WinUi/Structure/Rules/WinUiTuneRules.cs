// ── Themes/WinUi/Structure/Rules/WinUiTuneRules.cs — Tune's AI picture and the Tune strip's measures (WinUI) ─────────────────────────────────────────
//
// The owner, 2026-11-18: "make the tune button a lot more prevalent like an AI button". Design systems mark an AI action with a sparkle beside its word on
// the button system's own shape (Cloudscape, PatternFly, SAP), and Microsoft's Copilot entry points wear a blue → violet → pink stroke; WinUI's Tune takes
// that stroke on the button and the strip, the sparkle in its middle hue, and a faint wash of the same hues while the mix is tuned or the strip is open.

using FluentGpu.Foundation;

namespace Wavee.Themes.WinUi;

/// <summary>Tune's AI picture in WinUI (the button in the list bar and the hero, the strip) and the strip's measures.</summary>
internal static class WinUiTuneRules
{
    /// <summary>The AI stroke: blue → violet → pink, left to right.</summary>
    public static readonly GradientSpec Stroke = new(GradientShape.Linear, 0f,
    [
        new GradientStop(0f, ColorF.FromRgba(0x3B, 0x82, 0xF6)),
        new GradientStop(0.5f, ColorF.FromRgba(0x8B, 0x5C, 0xF6)),
        new GradientStop(1f, ColorF.FromRgba(0xEC, 0x48, 0x99)),
    ]);

    /// <summary>The lit button's wash (tuned, or the strip open): the stroke's hues, faint.</summary>
    public static readonly GradientSpec LitWash = new(GradientShape.Linear, 0f,
    [
        new GradientStop(0f, ColorF.FromRgba(0x3B, 0x82, 0xF6, 0x33)),
        new GradientStop(0.5f, ColorF.FromRgba(0x8B, 0x5C, 0xF6, 0x33)),
        new GradientStop(1f, ColorF.FromRgba(0xEC, 0x48, 0x99, 0x33)),
    ]);

    /// <summary>The strip's ground over the card fill: the hues fainter still.</summary>
    public static readonly GradientSpec StripWash = new(GradientShape.Linear, 0f,
    [
        new GradientStop(0f, ColorF.FromRgba(0x3B, 0x82, 0xF6, 0x14)),
        new GradientStop(0.5f, ColorF.FromRgba(0x8B, 0x5C, 0xF6, 0x14)),
        new GradientStop(1f, ColorF.FromRgba(0xEC, 0x48, 0x99, 0x14)),
    ]);

    /// <summary>The sparkle's ink: the stroke's middle hue, legible on light and dark grounds.</summary>
    public static readonly ColorF Sparkle = ColorF.FromRgba(0x9B, 0x6B, 0xFF);

    /// <summary>The stroke's width on the button and the strip.</summary>
    public const float StrokeWidth = 1f;

    /// <summary>The strip: its padding, the gap between its head and its chips, the head's gaps, the gap between chips (and wrapped lines).</summary>
    public const float StripPad = 16f, StripGap = 12f, HeadGap = 12f, ChipGap = 8f;

    /// <summary>The head's sparkle tile and its glyph.</summary>
    public const float TileEdge = 32f, TileGlyph = 16f;

    /// <summary>The hero's Tune: the gap between the sparkle and its word, and its side padding.</summary>
    public const float HeroGap = 8f, HeroPadX = 12f;

    /// <summary>The close button's edge, the working ring beside the line, and the gap between the title and its line.</summary>
    public const float CloseEdge = 32f, RingSize = 14f, RingGap = 6f, LineGap = 2f;
}
