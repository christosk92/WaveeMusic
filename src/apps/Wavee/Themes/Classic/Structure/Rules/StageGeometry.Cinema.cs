// ── Themes/Classic/Structure/Rules/StageGeometry.Cinema.cs — the numbers of Classic's stage in Video mode (Cinema) ───────────────
//
// The board "Classic · Cinema" (the video full screen's concept canvas, Main.dc.html): the picture edge to edge, a scrim under the top
// bar and one under the title and the transport, the title block (36/44 Display semibold over a 15 meta line) at the transport card's
// content edge above it. The top bar, the transport card and the chrome's motion are the stage's own (`ClassicStageGeometry`, the tier
// record `ClassicStageLayout`); what this file adds is the scrims and the title block. Literals live here (WVTH003 allows `Rules\**`).

namespace Wavee.Themes.Classic;

/// <summary>The constants of Classic's stage chrome over the picture (Video mode).</summary>
internal static class ClassicStageCinemaGeometry
{
    /// <summary>The scrim under the top bar: this tall, from the stage veil at <see cref="TopScrimA"/> to nothing (the board's 120 at 0.90;
    /// lighter, as the top bar keeps its own veil over it).</summary>
    public const float TopScrimH = 120f, TopScrimA = 0.72f;

    /// <summary>The scrim under the title and the transport: this share of the stage's height, from nothing to the veil at
    /// <see cref="BottomScrimA"/>, reached at <see cref="BottomScrimSolidAt"/> of the way down (the board's 480 of 810, 0.95 at 55 %).</summary>
    public const float BottomScrimFrac = 0.56f, BottomScrimA = 0.92f, BottomScrimSolidAt = 0.55f;

    /// <summary>The title: Display 36/44 semibold, two lines at most; the meta line ("Artist · Album") 15/20.</summary>
    public const float TitleSize = 36f, TitleLine = 44f, MetaSize = 15f, MetaLine = 20f;

    public const ushort TitleWeight = 600;

    public const int TitleMaxLines = 2;

    /// <summary>The gap between the title and the meta line (the board's 6).</summary>
    public const float TitleGap = 6f;

    /// <summary>The title block's gap above the transport card, and its widest share of the stage.</summary>
    public const float TitleAboveTransport = 24f, TitleMaxWFrac = 0.62f;

    /// <summary>The title block rises in from this far below with the chrome (and sinks back as it rests).</summary>
    public const float TitleEnterDy = 12f;
}
