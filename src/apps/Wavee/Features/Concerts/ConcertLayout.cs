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
    /// <summary>The Concerts treatment's audio-equalizer motif: a small cluster of vertical bars, Fluent-proportioned
    /// (even single-weight width, rounded pill caps) with a peak in the middle tapering at the edges so the cluster
    /// reads as "live audio" rather than a bar chart. Centered horizontally in the pane and bottom-anchored on a
    /// shared baseline so each bar can grow from its own foot on an <c>AnimChannel.ScaleY</c> pulse loop
    /// (<c>TransformOriginY = 1</c>). Fixed (not randomized) so a resize's remount always produces the SAME layout
    /// for the same box — deterministic, and testable.</summary>
    public static BrowseTile[] ConcertBars(float width, float height)
    {
        const int count = 4;
        float unit = MathF.Min(width, height);
        float barWidth = MathF.Max(4f, unit * 0.07f);
        float gap = barWidth * 0.85f;
        float totalWidth = count * barWidth + (count - 1) * gap;
        float startX = (width - totalWidth) * 0.5f;
        float baselineY = height * 0.70f;
        float maxBarHeight = MathF.Max(barWidth, unit * 0.30f);
        // A peak in the middle, tapering toward the edges — an equalizer read, not a staircase.
        ReadOnlySpan<float> heightFractions = [0.52f, 0.86f, 1f, 0.68f];

        var bars = new BrowseTile[count];
        for (int i = 0; i < count; i++)
        {
            float h = MathF.Max(barWidth, maxBarHeight * heightFractions[i]);
            float x = startX + i * (barWidth + gap);
            float y = MathF.Max(0f, baselineY - h);
            bars[i] = new BrowseTile(x, y, barWidth, h);
        }
        return bars;
    }

    /// <summary>The Browse treatment's stacked-layers motif: a few same-sized rounded rectangles offset along one
    /// diagonal, Fluent's "Stack" glyph shape — the back layer top-left, the front layer bottom-right. Each layer
    /// rides its own co-prime <c>TranslateX</c>/<c>TranslateY</c> loop that nudges it FURTHER along the same diagonal
    /// it is already offset on and back, so the stack gently fans open and settles rather than sitting static. Fixed
    /// (not randomized) so a resize's remount always produces the SAME layout for the same box — deterministic, and
    /// testable. Fractions overlap slightly without touching an edge, at any pane size ≥
    /// <see cref="WideEditorialMetrics.ArtworkMin"/>.</summary>
    public static BrowseTile[] BrowseTiles(float width, float height) =>
    [
        new BrowseTile(width * 0.16f, height * 0.40f, width * 0.46f, height * 0.42f),
        new BrowseTile(width * 0.26f, height * 0.28f, width * 0.46f, height * 0.42f),
        new BrowseTile(width * 0.36f, height * 0.16f, width * 0.46f, height * 0.42f),
    ];
}

public readonly record struct BrowseTile(float X, float Y, float Width, float Height);
