// ── Themes/Classic/Structure/Rules/ClassicConcertRules.cs — the numbers of Classic's concert surfaces (themes plan 6.1, T10b3) ───────────────────
//
// The literals that make the concert surfaces look the way they do (WVTH003 keeps them out of the views): the event tile and the artist page's upcoming card, the
// date block, the split hero's seam and photo, the filter bar's pills, the when / where flyouts and the location picker, the tour dashboard's board and its shy
// month pill, the event page's tickets and lineup, and the sub-page frame. 0.2.9 `ConcertUi.cs` / `ConcertFilterBar.cs` / `ConcertDateFlyout.cs`, verbatim.
//
// The width ladders (the schedule's and the event page's wide arm) and the grid's column floor are `Rules\Tiers.Concert.cs`.

namespace Wavee.Themes.Classic;

/// <summary>Classic's concert numbers.</summary>
internal static class ClassicConcertRules
{
    // ── focus and shared glyph ──
    /// <summary>The focus visual margin of a tile, a stub and a board tile.</summary>
    public const float FocusMargin = 2f;

    /// <summary>The placeholder glyph's alpha over a plate (the tile's and the hero's calendar).</summary>
    public const float GlyphAlpha = 0.72f;

    // ── the event tile ──
    /// <summary>The gap between the tile's three text lines.</summary>
    public const float TileTextGap = 3f;

    /// <summary>The tile cover's decode width.</summary>
    public const float TileArtDecode = 320f;

    /// <summary>How far the artwork-less tile's plate leans toward the show's accent.</summary>
    public const float TileAccentMix = 0.32f;

    /// <summary>The artwork-less tile's calendar glyph.</summary>
    public const float TileGlyph = 38f;

    // ── the artist page's upcoming card (the stub) ──
    public const float StubDateW = 48f, StubDaySize = 28f, StubDayLine = 36f, StubTextGap = 2f;
    public const float StubVenueSize = 14f, StubVenueLine = 20f, StubPinGap = 4f, StubPinSize = 12f, StubCitySize = 12f, StubCityLine = 16f;

    // ── the browse-all lead cell ──
    public const float BrowseAllGlyphAlpha = 0.30f;
    public const float BrowseAllSmallGlyph = 30f, BrowseAllPinGlyph = 44f, BrowseAllTinyGlyph = 22f;

    // ── the date block ──
    public const float DateBlockW = 56f, DateBlockH = 60f, DateBlockCompactW = 48f, DateBlockCompactH = 52f;

    // ── the split hero ──
    /// <summary>The wide copy pane's grow (the photo's is the metrics' media fraction, 0.44).</summary>
    public const float HeroCopyGrow = 0.56f;

    /// <summary>The photo's vertical focus, decode width and reveal fade.</summary>
    public const float HeroFocusY = 0.30f, HeroDecode = 1024f, HeroRevealMs = 220f;

    /// <summary>The photo-less pane's calendar glyph.</summary>
    public const float HeroGlyph = 52f;

    /// <summary>Where the seam gradient fades out: the wide arm's, and the narrow arm's start.</summary>
    public const float HeroSeamWide = 0.22f, HeroSeamNarrow = 0.68f;

    // ── the location button ──
    public const float LocationMinH = 32f, LocationGlyphBox = 20f, LocationPin = 16f, LocationChevronBoxW = 16f;

    // ── the filter bar's pills and tokens ──
    public const float PillHeight = 32f, PillGap = 6f, PillPadX = 12f, PillPadY = 5f, PillGlyph = 14f, PillChevron = 10f;
    public const float BarDividerH = 20f, BarScrollerH = 44f, BarEdgeFadeBand = 36f;
    public const float TokenReflowMs = 220f, PillReflowMs = 260f, SegmentDockMs = 300f, ChipExitMs = 220f;

    /// <summary>How far the "This weekend" chip travels into the pill (and the segment docks in from), and the segment's entering opacity.</summary>
    public const float ChipTravel = 56f, SegmentDockFrom = 0.4f;

    // ── the fused pill's accent register ──
    public const float AccentPillH = 32f, AccentPillSegmentH = 26f, AccentPillPadL = 3f, AccentPillPadT = 3f, AccentPillPadR = 12f, AccentPillSegPad = 11f;
    public const float AccentPillGap = 8f, AccentPillSegmentGap = 5f, AccentPillText = 14f, AccentPillCheck = 12f, AccentPillTrailing = 10f;

    // ── the count ticker ──
    public const float TickerNumberW = 64f, TickerGap = 2f;

    /// <summary>The ticker's tween length.</summary>
    public const double TickerTweenMs = 380.0;

    // ── the hub feed ──
    /// <summary>The most tiles a shelf shows.</summary>
    public const int ShelfMax = 16;

    /// <summary>The promo card's title lines.</summary>
    public const int PromoTitleLines = 2;

    /// <summary>The lazy grid's overscan rows.</summary>
    public const int GridOverscanRows = 3;

    /// <summary>The pending silhouette's shelf tiles and grid tiles, and the append tail's shimmering tiles.</summary>
    public const int SeedShelfTiles = 4, SeedGridTiles = 6, SeedTailTiles = 4;

    // ── the flyouts ──
    public const float FlyoutRowMinH = 40f, FlyoutLead = 16f, FlyoutTrailing = 14f, FlyoutCheck = 14f, FlyoutGap = 2f;
    public const float WhenFlyoutW = 320f, WhereFlyoutW = 264f;
    public const float MonthHeadMinH = 36f, MonthBackBox = 28f, MonthFootMinH = 44f, MonthMaxH = 400f;
    public const float CalendarCellW = 38f, CalendarCellH = 32f, CalendarHeadH = 20f, CalendarGap = 4f;

    // ── the location picker ──
    public const float PickerW = 360f, PickerFieldW = 340f, PickerFieldH = 32f, PickerResultsMaxH = 248f, PickerRowH = 48f, PickerPin = 18f;

    // ── the sub-page frame (the schedule and the event page) ──
    /// <summary>The body's side gutter and the extra bottom padding past the player dock.</summary>
    public const float SubPageGutter = 32f, SubPageBottomExtra = 40f;

    /// <summary>The measured-width fallbacks of the two dashboards before their first measure.</summary>
    public const float ScheduleFallbackWidth = 900f, DetailFallbackWidth = 1000f;

    // ── the tour dashboard ──
    public const float ScheduleLocationW = 220f, TourDatesSize = 28f, TourDatesLine = 36f, MonthTabsH = 48f;
    public const float BoardHeadMinH = 58f, BoardMonthSize = 20f, BoardMonthLine = 28f, BoardShowAllMinH = 44f, BoardChevron = 14f;
    public const float BoardTileMinH = 68f, BoardRailW = 3f, BoardRailH = 32f, BoardRailRadius = 2f, BoardTextGap = 2f, BoardTrailing = 16f, BoardPin = 12f;
    public const float BoardDayW = 44f, BoardRunDayW = 60f;

    // ── the shy month pill ──
    public const float PillPresenceH = 32f, PillPresenceMs = 200f, PillExitMs = 260f, PillEnterDy = 8f, PillExitDy = 6f;

    /// <summary>The offset's write floor (DIP), the sub-half-DIP noise it ignores and how far past the viewport top the board header may be and still count as crossed.</summary>
    public const float ScrollStepDip = 8f, PillStepNoise = 0.5f, PillCrossSlack = 4f;

    /// <summary>How long the pill lingers past the last step.</summary>
    public const float LingerMs = 1000f;

    // ── the event page ──
    public const float TicketsRailW = 320f, HeadlineGap = 4f, StatusPadY = 2f, FactGlyph = 16f;
    public const float ActAvatar = 44f, ActRowMinH = 60f, ActChevron = 14f;
}
