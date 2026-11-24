// ── Themes/Common/Rules/CommonColourEditorRules.cs — the custom colours editor's numbers (#184) ────────────────────────────────
//
// One design for every theme (the body takes its corners, type and casing from the active theme's roles): a 760 card (the window less a
// 16 gutter on each side below that), the six wells as 40-DIP rows (the Compact hit target, so the same rows at every tier), the colour
// picker's 220 spectrum, a fixed 300 x 200 preview (a drag repaints it, never re-lays it out), a two-line note whose height is reserved, and
// the preview beside the wells above Compact, under them at Compact (`CommonTiers.Stacks`).

namespace Wavee.Themes.Common;

internal static class CommonColourEditorRules
{
    /// <summary>The card's width, and its gutter to the window where the window is narrower.</summary>
    public const float CardWidth = 760f, WindowGutter = 16f;

    /// <summary>The card's padding and the gap between its blocks.</summary>
    public const float CardPadding = 24f, BlockGap = 16f;

    /// <summary>The title: 20/28 semibold.</summary>
    public const float TitleSize = 20f, TitleLine = 28f;

    /// <summary>A well row: 40 tall (the Compact target at every tier), its swatch 24, its label column 140, its inner gap 12.</summary>
    public const float WellRowHeight = 40f, Swatch = 24f, SwatchRing = 1f, WellLabelWidth = 140f, WellGap = 12f, WellPadX = 8f;

    /// <summary>The selected well's accent ring.</summary>
    public const float SelectedRing = 2f;

    /// <summary>The picker's spectrum.</summary>
    public const float Spectrum = 220f;

    /// <summary>The preview miniature: its size and its parts (the title strip, the pane, a card, the player bar, the text bars).</summary>
    public const float PreviewW = 300f, PreviewH = 200f, PreviewStrip = 16f, PreviewPane = 72f, PreviewBar = 28f, PreviewPad = 12f,
        PreviewCardW = 76f, PreviewCardH = 44f, PreviewGap = 8f, PreviewLineH = 6f, PreviewHeadW = 120f, PreviewHeadH = 10f, PreviewSubW = 90f,
        PreviewNavW = 44f, PreviewButtonW = 52f, PreviewButtonH = 18f, PreviewLinkW = 40f, PreviewSeekW = 150f, PreviewSeekH = 3f, PreviewRadius = 4f;

    /// <summary>The note: two 12/16 lines, reserved even when empty so the buttons never move.</summary>
    public const float NoteSize = 12f, NoteLine = 16f, NoteLines = 2f;

    /// <summary>The Start from combo's width.</summary>
    public const float StartFromWidth = 200f;
}
