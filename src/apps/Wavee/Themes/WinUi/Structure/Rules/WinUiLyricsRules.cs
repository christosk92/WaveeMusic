// ── Themes/WinUi/Structure/Rules/WinUiLyricsRules.cs — the numbers of WinUI's lyrics surface (themes plan 6.2; P6 W3) ───────
//
// What `Shell/Lyrics.UI.cs` held as literals beside the picture, moved verbatim with it: the stage's reading column, the centred state
// line, the "no sync during video" note, the resync pill and its ring, the podcast peek's gate, the breathing dots' entrance, the
// accent pill on the stage's active row and the secondary line's weight. The surface metrics (row type and rhythm, gutters, the
// loading bars, the focal band, the halo sigmas, the scale ladder) are `Lyrics.Surface`'s and the ladders, ramps and clocks are the
// core's: the frame driver and the follow are budgeted from them, so the views read them by name.

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

    // ── the loading bars ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The skeleton style's text ratio (inert under the custom shimmer, kept as it was).</summary>
    public const float SkeletonTextRatio = 0.86f;
}
