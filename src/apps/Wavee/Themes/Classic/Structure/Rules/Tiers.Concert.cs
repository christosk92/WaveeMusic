// ── Themes/Classic/Structure/Rules/Tiers.Concert.cs — the concert surfaces' width ladders (themes plan 3.6; T10b3) ──────────────────────────────
//
// `ConcertLayout` from `Entities\Concert.Rules.cs` (0.2.9 `ConcertLayout.cs`, verbatim), moved out of core (plan 3.6: each move PR moves its own view's ladders
// into `Rules\Tiers.<Family>.cs`). Only Classic's concert views read it: the artist's schedule (the hero's media pane and the boards' columns go wide at 760 and
// leave at 720), the event page (the tickets rail at 920, leaving at 860), the split hero's metrics and the all-events grid's column floor. Structural changes use
// separate enter / leave thresholds so a continuously resizing window cannot flap component subtrees around one boundary. The views answer the ladder from the
// page's own measured width and compare nothing themselves (WVTH013).
//
// Public, because `ConcertLayoutTests`, `ConcertSchedulePageTests` and `ConcertDetailModelTests` pin the ladders.

namespace Wavee.Themes.Classic;

/// <summary>Classic's concert layout ladders and grid numbers.</summary>
public static class ClassicConcertTiers
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

    /// <summary>The event tile's chrome over its cell width (ch 17 §9 item 5, derived): pad-top 8 + art (cellW − 16) +
    /// gap 8 + text (16 + 3 + 20 + 3 + 16) + pad-bottom 12 = cellW + 70.</summary>
    public const float EventChrome = 70f;

    /// <summary>The all-events grid's minimum column width and its two gaps (ch 17 §3). Row extra = chrome + row gap = 86.</summary>
    public const float GridMinColumn = 240f, GridColumnGap = 12f, GridRowGap = 16f;

    /// <summary>The grid's row extra over the cell width.</summary>
    public const float GridRowExtra = EventChrome + GridRowGap;
}

/// <summary>The split hero's measures: the wide row's height and the narrow pane's media height, the photo's share and the copy padding.</summary>
public readonly record struct EditorialHeroMetrics(float Height, float MediaHeight, float MediaFraction, float Padding);

/// <summary>The wide editorial card's measures (0.2.9 <c>WideEditorial</c>).</summary>
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
