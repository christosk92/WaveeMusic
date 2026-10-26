// ── Themes/WinUi/Structure/Rules/WinUiPaneLyricsRules.cs — the numbers of the side pane's lyrics (board W-PaneLyrics; P6 player polish) ─────
//
// The pane's own reading form (`WinUiPaneLyrics`), not the stage's: crisp lines (no depth blur, no opacity ladder, no halo), the board's
// inks and type, the active line a step larger, followed a quarter of the way down the pane. Literal numbers live here (WVTH003 exempts
// `Rules\**`).

using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.WinUi;

/// <summary>The constants of the side pane's lyrics.</summary>
internal static class WinUiPaneLyricsRules
{
    /// <summary>The pane's form: the active line's content centres a quarter of the way down the lyrics viewport (W-PaneLyrics: three sung
    /// lines above it under the 24 top inset), never depth-blurred.</summary>
    public static readonly LyricsRailForm Form = new(Band: 0.25f, Crisp: true);

    /// <summary>A line: Body Large Strong 18/24 semibold; the active one reads 20/28 (drawn as a 20/18 scale of the same run, so activating a
    /// line never re-wraps or re-lays the column out).</summary>
    public const float LineSize = 18f, LineHeight = 24f, ActiveScale = 20f / 18f;

    public const ushort LineWeight = 600, SecondWeight = 400;

    /// <summary>The column: 24 either side; 14 between the lines (7 above and below each row, the measured seam reads the border box); the
    /// active line's grown run needs <see cref="ScaleRoom"/> more at the trailing edge to stay inside the column.</summary>
    public const float SidePad = 24f, RowPad = 7f, RowTouchPad = 8f, ScaleRoom = 32f;

    /// <summary>A row's pad above and below in a window tier: 8 at Compact (a one-line row is the 40 touch floor), else 7.</summary>
    public static float RowPadOf(WidthTier window) => window == WidthTier.Compact ? RowTouchPad : RowPad;

    /// <summary>The second line (translation / romanization): Body 14/20 in the secondary ink, 4 under its lyric.</summary>
    public const float SecondSize = 14f, SecondLine = 20f, SecondGap = 4f;
}
