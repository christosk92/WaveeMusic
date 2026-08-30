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
