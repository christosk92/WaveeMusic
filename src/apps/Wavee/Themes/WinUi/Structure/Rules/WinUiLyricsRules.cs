// ── Themes/WinUi/Structure/Rules/WinUiLyricsRules.cs — the numbers of WinUI's lyrics surface (themes plan 6.2; P6 W3) ───────
//
// What `Shell/Lyrics.UI.cs` held as literals beside the picture, moved verbatim with it: the stage's reading column, the centred state
// line, the "no sync during video" note, the resync pill and its ring, the podcast peek's gate, the breathing dots' entrance, the
// accent pill on the stage's active row and the secondary line's weight. The surface metrics (row type and rhythm, gutters, the
// loading bars, the focal band, the halo sigmas, the scale ladder) are `Lyrics.Surface`'s and the ladders, ramps and clocks are the
// core's: the frame driver and the follow are budgeted from them, so the views read them by name.

using Wavee.Theming.Contracts;

namespace Wavee.Themes.WinUi;

/// <summary>The constants of WinUI's lyrics surface and lines.</summary>
internal static class WinUiLyricsRules
{
    // ── the stage's reading column ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>The immersive stage's reading column: capped at 1000 (the prototype's <c>.ln { max-width: 1000px }</c>) times the
    /// stage's type scale, left-anchored, its gutter spelled as TRAILING padding (the leading air is the stage's pane region gap).</summary>
    public const float StageColumnMaxW = 1000f, StageColumnGutter = 24f;

    // ── the state line ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A centred one-line state ("No lyrics available", "Nothing playing").</summary>
    public const float MessageSize = 14f, MessageLineHeight = 20f;

    // ── the offline voice (tuned R2) ───────────────────────────────────────────────────────────────────────────────

    /// <summary>The offline voice's caption wraps at this width (the board's 240), under the 20/28 headline.</summary>
    public const float OfflineCaptionMaxW = 240f;

    // ── the video note ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The note a video raises while it suppresses timed sync.</summary>
    public const float VideoNoteSize = 12f;

    public const ushort NoteWeight = 600;

    // ── the resync pill ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The "Resync" pill: the gap between ring and label, its padding (left, top, right, bottom), corner radius and the
    /// countdown ring's size.</summary>
    public const float ResyncGap = 8f, ResyncPadLeft = 11f, ResyncPadTop = 7f, ResyncPadRight = 13f, ResyncPadBottom = 7f,
        ResyncRadius = 16f, ResyncRingSize = 18f;

    /// <summary>The pill's label.</summary>
    public const float ResyncLabelSize = 12f, ResyncLabelLineHeight = 16f;

    /// <summary>The pill rises from this far below when it comes and sinks back when it goes.</summary>
    public const float ResyncRiseDy = 4f;

    // ── the podcast peek's gate ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The quiet "length · language" line under the primary pill.</summary>
    public const float PeekMetaSize = 12f, PeekMetaLineHeight = 16f;

    // ── the breathing dots ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The dots come in from (and go back to) this scale.</summary>
    public const float DotsEnterScale = 0.72f;

    /// <summary>A breathing dot is a circle: its corner radius is this fraction of its size.</summary>
    public const float DotRadiusFraction = 0.5f;

    // ── the rows ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The ListView-style accent pill on the stage's active row, in the gutter.</summary>
    public const float PillWidth = 3f, PillHeight = 28f, PillRadius = 3f;

    /// <summary>The translation / romanization under a music lyric.</summary>
    public const ushort SecondaryWeight = 600;

    // ── the stage's music rows (W-Lyrics, W-Lyrics-Small, W-Lyrics-Huge, W-Lyrics-Compact) ──────────────────────────────

    /// <summary>The core's stage type at scale 1 (<c>Lyrics.Surface.Timed(large: true)</c>): the stage's type scale is the tier's lyric type over it.</summary>
    public const float StageBaseType = 36f;

    /// <summary>The board's rows at the Standard stage type 28: a line 28 / 36, the ACTIVE line 34 / 44, all at 600 (Display face), 14 between lines.
    /// The row lays out at the active size and an inactive row scales to <see cref="StageInactiveScale"/> (the size step glides, never relayouts);
    /// the pad is what leaves the board's 14 between two scaled lines.</summary>
    public const float StageBoardType = 28f, StageActiveType = 34f, StageActiveLine = 44f, StageRowPad = 3f, StageSidePad = 24f;

    public const ushort StageWeight = 600;

    /// <summary>A row is a seek target: never shorter than the Compact target floor (the pad grows to reach it on the Compact stage's 20 type).</summary>
    public const float StageRowMinH = 40f;

    /// <summary>An inactive row's scale: the board's 28 under its 34 active line.</summary>
    public const float StageInactiveScale = StageBoardType / StageActiveType;

    /// <summary>The active row centres in the column (the board's column is centred in the band between the bar and the transport card).</summary>
    public const float StageBand = 0.5f;

    /// <summary>The board's 7 lines: the active line and three either side; a farther line is transparent (and unblurred: nobody sees it).</summary>
    public const int StageRing = 3;

    /// <summary>The far lines' fade (W-Lyrics: <c>max(0.25, 1 - 0.22 d)</c>) and blur (σ 0.4 per ring step).</summary>
    public const float StageFadeStep = 0.22f, StageFadeFloor = 0.25f, StageSigmaStep = 0.4f;

    /// <summary>The stage's music rows for the core (<c>LyricsLinesStyle.StageRows</c>).</summary>
    public static LyricsStageRows StageRows { get; } = new(StageMetrics, StageBand, StageSigma);

    /// <summary>The row metrics at stage type scale <paramref name="k"/>: the board's 28-type numbers scaled to the tier's type (Compact 20 → 24 / 31,
    /// Small 22 → 27 / 35, Huge 36 → 44 / 57), the side gutter unscaled.</summary>
    public static Lyrics.RowMetrics StageMetrics(float k)
    {
        float s = StageBaseType * k / StageBoardType;
        float line = MathF.Round(StageActiveLine * s);
        float pad = MathF.Max(MathF.Round(StageRowPad * s), MathF.Ceiling((StageRowMinH - line) * 0.5f));
        return new Lyrics.RowMetrics(MathF.Round(StageActiveType * s), line, pad, StageSidePad, StageWeight);
    }

    /// <summary>The σ for ring distance <paramref name="d"/>: 0.4 per step inside the ring, 0 outside it (those lines are transparent).</summary>
    public static float StageSigma(int d) => d <= 0 || d > StageRing ? 0f : StageSigmaStep * d;

    /// <summary>The row opacity for ring distance <paramref name="d"/>: 1 on the active line, the board's fade inside the ring, 0 outside it.</summary>
    public static float StageOpacity(int d) => d <= 0 ? 1f : d > StageRing ? 0f : MathF.Max(StageFadeFloor, 1f - StageFadeStep * d);

    // ── the loading bars ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The skeleton style's text ratio (inert under the custom shimmer, kept as it was).</summary>
    public const float SkeletonTextRatio = 0.86f;
}
