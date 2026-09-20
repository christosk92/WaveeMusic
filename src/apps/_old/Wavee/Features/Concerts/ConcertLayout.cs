namespace Wavee.Features.Concerts;

/// <summary>Pure responsive decisions for the concert surfaces. Structural changes use separate enter/leave thresholds
/// so a continuously resizing window cannot flap component subtrees around one boundary.</summary>
public static class ConcertLayout
{
    public const float ScheduleEnterWide = 760f;
    public const float ScheduleLeaveWide = 720f;
    public const float EditorialHeroEnterWide = 760f;
    public const float EditorialHeroLeaveWide = 720f;
    public const float DetailEnterWide = 920f;
    public const float DetailLeaveWide = 860f;

    public static bool ScheduleWide(float width, bool wasWide, bool initialized = true) =>
        !initialized ? width >= ScheduleEnterWide
        : wasWide ? width >= ScheduleLeaveWide : width >= ScheduleEnterWide;

    public static bool DetailWide(float width, bool wasWide, bool initialized = true) =>
        !initialized ? width >= DetailEnterWide
        : wasWide ? width >= DetailLeaveWide : width >= DetailEnterWide;

    public static bool EditorialHeroWide(float width, bool wasWide, bool initialized = true) =>
        !initialized ? width >= EditorialHeroEnterWide
        : wasWide ? width >= EditorialHeroLeaveWide : width >= EditorialHeroEnterWide;

    public static EditorialHeroMetrics EditorialHero(bool wide) => wide
        ? new EditorialHeroMetrics(Height: 320f, MediaHeight: 320f, MediaFraction: 0.44f, Padding: 28f)
        : new EditorialHeroMetrics(Height: 0f, MediaHeight: 180f, MediaFraction: 1f, Padding: 20f);

    public static WideEditorialMetrics WideEditorial(float width) => width switch
    {
        >= 900f => new(Height: 288f, ArtworkFraction: 0.38f, ArtworkMin: 280f, ArtworkMax: 420f,
            Padding: 28f, SubtitleLines: 3),
        >= 600f => new(Height: 240f, ArtworkFraction: 0.42f, ArtworkMin: 220f, ArtworkMax: 360f,
            Padding: 24f, SubtitleLines: 2),
        _ => new(Height: 220f, ArtworkFraction: 0.55f, ArtworkMin: 180f, ArtworkMax: 280f,
            Padding: 20f, SubtitleLines: 2),
    };
}

public readonly record struct EditorialHeroMetrics(
    float Height,
    float MediaHeight,
    float MediaFraction,
    float Padding);

public readonly record struct WideEditorialMetrics(
    float Height,
    float ArtworkFraction,
    float ArtworkMin,
    float ArtworkMax,
    float Padding,
    int SubtitleLines)
{
    public float ArtworkWidth(float availableWidth) =>
        Math.Clamp(availableWidth * ArtworkFraction, ArtworkMin, Math.Min(ArtworkMax, availableWidth));
}

// ── #86 — the procedural promo-card art's geometry ──────────────────────────────────────────────────────────────────
// BCL-only (no PathData/engine types) so it stays testable without a source-text test: `EditorialArt` (ConcertUi.cs)
// freezes this geometry at mount from its own measured artWidth/height (component-props-contract.md — baked into the
// element Key so a resize remounts rather than keeping stale geometry) and turns it into engine primitives; this class
// only ever does the arithmetic.
public static class EditorialArtGeometry
{
    const double DegToRad = Math.PI / 180.0;

    /// <summary>One circular-arc segment, in the node-local DIP space <c>PathEl.Geometry</c> expects. Two of these
    /// (an outer sweep, an inner counter-sweep) are the Concerts treatment's "stage light" read, each on its own
    /// <c>AnimChannel.StrokeTrimStart/End</c> draw-on loop.</summary>
    public static ArcSweep Arc(float cx, float cy, float r, float startDeg, float sweepDeg)
    {
        double a0 = startDeg * DegToRad, a1 = (startDeg + sweepDeg) * DegToRad;
        float x0 = cx + r * (float)Math.Cos(a0), y0 = cy + r * (float)Math.Sin(a0);
        float x1 = cx + r * (float)Math.Cos(a1), y1 = cy + r * (float)Math.Sin(a1);
        int largeArc = MathF.Abs(sweepDeg) > 180f ? 1 : 0;
        int sweepFlag = sweepDeg >= 0f ? 1 : 0;
        return new ArcSweep(x0, y0, x1, y1, r, largeArc, sweepFlag);
    }

    /// <summary>The Concerts treatment's outer sweep — a broad arc low in the pane, biased toward the bottom so it
    /// reads as a stage horizon rather than a halo around the copy.</summary>
    public static ArcSweep ConcertArcOuter(float width, float height)
    {
        float cx = width * 0.5f, cy = height * 0.62f;
        float r = MathF.Min(width, height) * 0.62f;
        return Arc(cx, cy, r, startDeg: -150f, sweepDeg: 120f);
    }

    /// <summary>The Concerts treatment's inner counter-sweep — a tighter arc, swept the OPPOSITE direction, so the
    /// pair reads as two beams crossing rather than one arc traced twice.</summary>
    public static ArcSweep ConcertArcInner(float width, float height)
    {
        float cx = width * 0.5f, cy = height * 0.62f;
        float r = MathF.Min(width, height) * 0.46f;
        return Arc(cx, cy, r, startDeg: 40f, sweepDeg: -110f);
    }

    /// <summary>The Browse treatment's category mosaic: three rounded tiles at fixed relative fractions of the pane,
    /// each riding its own co-prime <c>TranslateY</c> wobble loop. Fixed (not randomized) so a resize's remount
    /// always produces the SAME layout for the same box — deterministic, and testable. Fractions overlap slightly
    /// without touching an edge, at any pane size ≥ <see cref="WideEditorialMetrics.ArtworkMin"/>.</summary>
    public static BrowseTile[] BrowseTiles(float width, float height) =>
    [
        new BrowseTile(width * 0.10f, height * 0.14f, width * 0.40f, height * 0.46f),
        new BrowseTile(width * 0.42f, height * 0.30f, width * 0.34f, height * 0.40f),
        new BrowseTile(width * 0.30f, height * 0.58f, width * 0.30f, height * 0.34f),
    ];
}

/// <summary>One circular-arc segment. <see cref="ToPathData"/> is the SVG path-data fragment
/// <c>PathDataParser.Parse</c> consumes directly to build a <c>PathEl.Geometry</c>.</summary>
public readonly record struct ArcSweep(float X0, float Y0, float X1, float Y1, float Radius, int LargeArc, int SweepFlag)
{
    public string ToPathData() => string.Format(
        System.Globalization.CultureInfo.InvariantCulture,   // a decimal-comma locale must never corrupt the token stream
        "M{0:F2},{1:F2} A{2:F2},{2:F2} 0 {3},{4} {5:F2},{6:F2}",
        X0, Y0, Radius, LargeArc, SweepFlag, X1, Y1);
}

public readonly record struct BrowseTile(float X, float Y, float Width, float Height);
