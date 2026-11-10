// ── Themes/Classic/Structure/Rules/StageGeometry.Cinema.cs — the numbers of Classic's stage in Video mode (Cinema) ───────────────
//
// The board "Classic · Cinema" (the video full screen's concept canvas, Main.dc.html): the picture edge to edge, a scrim under the top
// bar and one under the title and the transport, the title block (36/44 Display semibold over a 15 meta line) at the transport card's
// content edge above it. The top bar, the transport card and the chrome's motion are the stage's own (`ClassicStageGeometry`, the tier
// record `ClassicStageLayout`); what this file adds is the scrims and the title block. Literals live here (WVTH003 allows `Rules\**`).
//
// THEATRE (the board "Classic · Theatre", Classic-Theatre.dc.html): under the top bar, the picture keeps its 16:9 shape in the left region
// with the title block (28/36 Display semibold over a 14 meta line) under it, and the panel (Lyrics or Up next, the tier's width:
// `StageTierMetrics.TheatrePaneW`) docks on the right; the region runs from 8 under the top bar down to a gutter above the transport card, at
// the transport card's gutter on both sides and between the two. The picture and its title sit centred in the left region's height.

using FluentGpu.Foundation;

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

/// <summary>The constants and rects of Classic's stage in Video mode's Theatre (the picture with a panel beside it).</summary>
internal static class ClassicStageTheatreGeometry
{
    /// <summary>The region's top under the top bar (the board's 8).</summary>
    public const float RegionTop = 8f;

    /// <summary>The picture's shape: the region keeps a 16:9 box (a picture of another shape letterboxes inside it).</summary>
    public const float PictureAspect = 16f / 9f;

    /// <summary>The gap between the picture and its title block (the board's 14).</summary>
    public const float TitleGap = 14f;

    /// <summary>The title: Display 28/36 semibold, one line; the meta line ("Artist · Album") 14/20, 4 under it.</summary>
    public const float TitleSize = 28f, TitleLine = 36f, MetaSize = 14f, MetaLine = 20f, MetaGap = 4f;

    public const ushort TitleWeight = 600;

    /// <summary>The title block's height (the title line, the gap, the meta line).</summary>
    public const float TitleBlockH = TitleLine + MetaGap + MetaLine;

    /// <summary>The panel's padding (the board's 16) and how far it slides in from the right (the Huge 4K Up next column's slide).</summary>
    public const float PanePad = 16f, PaneSlide = 32f;

    /// <summary>The picture's rect, its title block's and the panel's, in stage DIP, for the stage layout <paramref name="L"/>.</summary>
    public readonly record struct Rects(RectF Picture, RectF Title, RectF Pane);

    public static Rects Of(Stage.Layout L)
    {
        float gutter = L.TransportInset;
        float top = Stage.Layout.TopBarH + RegionTop;
        float bottom = L.TransportTop - gutter;
        float regionH = MathF.Max(0f, bottom - top);
        float paneW = L.TheatrePaneW;
        float paneX = L.W - gutter - paneW;
        float columnW = MathF.Max(0f, paneX - 2f * gutter);
        float roomH = MathF.Max(0f, regionH - TitleGap - TitleBlockH);
        float picW = columnW, picH = MathF.Round(columnW / PictureAspect);
        if (picH > roomH) { picH = roomH; picW = MathF.Round(roomH * PictureAspect); }
        float x = gutter + MathF.Round((columnW - picW) * 0.5f);
        float y = top + MathF.Max(0f, MathF.Round((regionH - (picH + TitleGap + TitleBlockH)) * 0.5f));
        return new Rects(new RectF(x, y, picW, picH), new RectF(x, y + picH + TitleGap, picW, TitleBlockH), new RectF(paneX, top, paneW, regionH));
    }
}
