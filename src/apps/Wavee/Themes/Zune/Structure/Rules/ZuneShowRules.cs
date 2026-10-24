// ── Themes/Zune/Structure/Rules/ZuneShowRules.cs — the numbers and records of Zune's show, episode and concert pages (themes plan 6.3; P5 Z6b2) ──
//
// Boards Z-Show (+ -Compact, -Small, -Huge), Z-Episode (+ -Compact, -Small, -Huge) and Z-Concert (+ -Compact, -Small, -Huge). WVTH003 keeps every
// number that makes a look out of the views (`Views\Show`, `Views\Episode`, `Views\Concert`): they are here. Three families, one file:
//
//   · `ZuneShowRules`     the show reader (its rhythm, the visit head's cards, the ledger and the rail's small words). The arithmetic the list's
//                         seeds and the skeleton must agree on row for row is the core's (`ShowReaderRules`: the 48 rail, the 30 + 12 header, the
//                         14 + 24 + 4 month header, the gutters, the strip's 30) and is NOT repeated here;
//   · `ZuneEpisodeRules`  the episode page (the reader's measure, a chapter row, a comment, the reaction pill, the flyout);
//   · `ZuneConcertRules`  the concert pages, and `ZuneConcertTiers` / `ZuneConcertLayout` their records per CONTENT tier:
//
//       tier      title             date numeral   tickets               lineup / related
//       Compact   64/72 one line    64, beside the the slab stacked,     rows (64 / 56), no plate
//                 (bleed or fit)    month           bled to the gutters
//       Small     40/48             64             260 beside the facts  rows
//       Standard  64/72             64             320 beside the facts  a line of faces; related four across on the plate
//       Huge      150/160           150            440                   the same, roomier
//       Huge 4K   150/160           150            480                   the same, the 220 grid columns
//
// The hub and the schedule have no board (owner Q18) and read the same records (the filter pills are 40 tall at Compact, 32 elsewhere). The views
// read these records by the tier of the content they are drawn in; nothing here compares a width.
//
// Public, so `Wavee.Tests` pins the rules (the assembly has no InternalsVisibleTo).

using System.Collections.Immutable;
using FluentGpu.Animation;
using FluentGpu.Foundation;
using Wavee.Theming;
using Wavee.Theming.Roles;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Zune;

/// <summary>Zune's show page geometry (DIP).</summary>
public static class ZuneShowRules
{
    // ── the section rhythm ──

    /// <summary>The head's air above, and the gap between its sections.</summary>
    public const float HeadTop = 12f, SectionGap = 28f;

    /// <summary>The gap between a head word and its small count, the count's lift onto the word's baseline and the stacked sub line's gap.</summary>
    public const float HeadGap = 12f, SubLineLift = 4f, StackedGap = 2f;

    /// <summary>The reading measure of the about and of the rail's publisher line when the rail states none, and the about's size (15/22 in the boards).</summary>
    public const float AboutMeasure = 620f, AboutSize = 15f;

    /// <summary>The pinned month's plate and one year's row in the strip.</summary>
    public const float StickyMonthPlateH = 20f, YearRowHeight = 15f;

    /// <summary>Whether the year strip stands with its years (V19): in the frame's outer margin past the column edge (<paramref name="ownsEdge"/> false),
    /// at Standard and up, whose 48 holds four digits of the micro voice on a 30 row. Under Standard the margin (Compact's 16, Small's 24) would clip
    /// them and leave rows no finger can take, so there, and inside a reader's own gutter, the month headers open the jump list instead (the owner's
    /// call, 2026-10-24: Windows Phone's semantic zoom).</summary>
    public static bool YearsInStrip(WidthTier window, bool ownsEdge) => !ownsEdge && window >= WidthTier.Standard;

    // ── the month jump list (`ZuneShowJumpList.cs`) ──

    /// <summary>A month tile's height, the gap between tiles and rows, its inner padding, the air above a year after the first, and the tiles a row holds
    /// (four at Compact, six above it: a tile is never narrower than 40).</summary>
    public const float JumpTileH = 72f, JumpGap = 8f, JumpTilePad = 8f, JumpYearGap = 24f;

    /// <summary>The tiles a row of the jump grid holds.</summary>
    public static int JumpColumns(WidthTier window) => window == WidthTier.Compact ? 4 : 6;

    /// <summary>The tiles' entrance: each grows from this scale while it fades in, after (row + column) x the stagger, the wave capped at that many steps.</summary>
    public const float JumpTileScale = 0.86f, JumpStaggerMs = 18f;
    public const int JumpStaggerCap = 12;

    /// <summary>The grid's zoom back on close: its length and the scale it grows to while it fades.</summary>
    public const float JumpCloseMs = 220f, JumpCloseScale = 1.06f;

    /// <summary>The tiles' entrance clock (the pivot slide's, on the smooth-out curve; reduced motion keeps the fade).</summary>
    public static MotionTokenDef JumpTileMotion => MotionTokenDef.Eased(ZuneMotion.PivotSlideMs, Easing.SmoothOut, ReducedMotionPolicy.KeepFade);

    /// <summary>The month header's target reaches this far into the air above its line and the foot below it (24 + 16 = the 40 floor; the row's extent is
    /// unchanged).</summary>
    public const float HeaderTargetPad = 8f;

    /// <summary>The browser Back key (VK_BROWSER_BACK), which closes the jump list as Escape does.</summary>
    public const int BrowserBackKey = 166;

    /// <summary>The month header's target, the grid, and a tile's key prefix.</summary>
    public const string MonthHeaderKey = "rd:group:head", JumpGridKey = "zune-jump-grid", JumpTilePrefix = "zune-jump:";

    /// <summary>The cold skeleton's seed rows.</summary>
    public const int SeedRows = 8;

    /// <summary>A skeleton bar's radius.</summary>
    public const float SkelRadius = 2f;

    // ── the toolbar ──

    /// <summary>The toolbar's body (the core's plane is <c>ShowReaderRules.RailExtent</c>, the body centres in it), a glyph control's edge (the stage
    /// rule's own <c>ShowToolbarLayout.Glyph</c>: it sums the controls' widths), the glyph, the hairline between find and sort, the folded filter's
    /// chevron, the find box's floor and its open-field width.</summary>
    public const float ToolbarHeight = 40f, ControlEdge = ShowToolbarLayout.Glyph, ToggleGlyph = 14f, DividerHeight = 16f, FilterChevron = 10f,
                       FindMinHeight = 32f, FindFillMax = 9999f;

    /// <summary>A glyph control's edge under the reader's narrow arm: the Compact 40 target (a 30 glyph's slop met the list's scroll bar lane at the
    /// row's end: MultiSelect 30 x 30). The style states it (<c>ShowReaderStyle.NarrowToolbarGlyph</c>), so the stage rule sums 40 there too.</summary>
    public const float NarrowControlEdge = ZuneListRules.CompactTarget;

    // ── the visit head ──

    /// <summary>The head's shimmer: its title bar's width and the card's height.</summary>
    public const float HeadSeedTitleWidth = 160f, HeroSeedHeight = 72f;

    /// <summary>The card (Z-Show's continue and up-next items): the 72 cover, the gap beside it, the lines' gap, the label's size and line, the 40 ring.</summary>
    public const float CardArt72 = 72f, CardGap = 14f, CardTextGap = 2f, CardLabelSize = 12f, CardLabelLine = 16f, CardRing = 40f, CardRingGlyph = 14f;

    /// <summary>The continue card's progress track: its height (2, like the play bar's seek row) and its longest run.</summary>
    public const float HeroRuleHeight = 2f, HeroTrackMax = 420f;

    /// <summary>The up-next card's numeral column (the light numeral lead).</summary>
    public const float MiniNumeralW = 34f;

    /// <summary>The seed's U+2007 runs that make the derived shimmer draw a title bar and a meta bar.</summary>
    public const int MiniSeedTitleRun = 18, MiniSeedMetaRun = 10;

    // ── the rail's small words ──

    /// <summary>The ledger: the gap between its bar and its line, the bar (3), the line's size and height, and how long its parts take to ease.</summary>
    public const float LedgerGap = 6f, LedgerBar = 3f, LedgerSize = 12f, LedgerLine = 16f, LedgerSlideMs = 250f;

    /// <summary>The ledger's parts ease their widths when a mark-played moves them (a flex share changing).</summary>
    public static readonly LayoutTransition LedgerSlide = new(
        TransitionChannels.Position | TransitionChannels.Size, TransitionDynamics.Tween(LedgerSlideMs, Easing.SmoothOut),
        Size: SizeMode.Reflow, Axes: SizeAxes.Width);

    /// <summary>The rating line: the gap between its parts, the star, the line's floor.</summary>
    public const float RatingGap = 6f, RatingStar = 13f, RatingMinHeight = 24f;

    /// <summary>The badge words' gap and the boxed E's edge.</summary>
    public const float BadgeGap = 8f, ExplicitBox = 16f;

    /// <summary>The topic words: the gap between two and a topic's hit height.</summary>
    public const float TopicGap = 14f, TopicTarget = 32f;
}

/// <summary>Zune's episode page geometry (DIP).</summary>
public static class ZuneEpisodeRules
{
    // ── the slots ──

    /// <summary>The ring's label arrives as "Resume · 23 min left": the word and the quiet line split at this.</summary>
    public const string LabelSplit = " · ";

    /// <summary>The show link under the cover (Z-Episode: 15), and the rail's short about (13, clamped to this many lines).</summary>
    public const float AttributionSize = 15f, RailAboutSize = 13f;

    /// <summary>The rail's About block clamps to this many lines.</summary>
    public const int RailAboutLines = 4;

    /// <summary>The show rating popover's width and its stars' size.</summary>
    public const float RatingWidth = 250f, RatingStar = 24f;

    // ── the reader ──

    /// <summary>The reader's column key (the tests find it).</summary>
    public const string ReaderKey = "zune.episode.reader";

    /// <summary>The reader's air above its first heading and below the last section (past the player dock's reserve).</summary>
    public const float ReaderTop = 8f, ReaderBottom = 16f;

    /// <summary>The reading measure of a section, of the chapters and of a comment thread.</summary>
    public const float ReaderMeasure = 760f, ThreadMeasure = 760f;

    /// <summary>The transcript section's height (its view scrolls inside the page's scroll).</summary>
    public const float TranscriptHeight = 560f;

    /// <summary>The reader scroll's edge fade band.</summary>
    public const float EdgeFadeBand = 24f;

    /// <summary>The description's size (15/22 in Z-Episode) and its clamp, and the seed's line count.</summary>
    public const float AboutSize = 15f;

    /// <summary>See <see cref="AboutSize"/>.</summary>
    public const int AboutLines = 8, SeedLines = 8;

    // ── a chapter row (Z-Episode: a 44 numeral column, 56 high) ──

    /// <summary>The row's height, its numeral column and the numeral's inset from the row's edge.</summary>
    public const float ChapterRowH = 56f, ChapterNumeralW = 44f, ChapterNumeralInset = 4f;

    /// <summary>The start time under the title (12 on a 16 line) and the playing row's equalizer beside it.</summary>
    public const float ChapterTimeSize = 12f, ChapterTimeLine = 16f, ChapterEqualizer = 11f;

    // ── a comment ──

    /// <summary>The avatar of a top-level comment and of a reply.</summary>
    public const float ThreadAvatar = 32f, ReplyAvatar = 24f;

    /// <summary>The comment body's rung (15/22).</summary>
    public const float CommentSize = 15f, CommentLine = 22f;

    /// <summary>The name line: the author (14 / 20) and the date (12), both on a 20 line.</summary>
    public const float AuthorSize = 14f, DateSize = 12f, AuthorLine = 20f;

    /// <summary>The reaction pill's emoji, their overlap, and the label's rung.</summary>
    public const float PillEmojiSize = 14f, PillEmojiLine = 16f, PillEmojiOverlap = -4f, PillSize = 12f, PillLine = 16f;

    /// <summary>The pill's accent wash at rest, hovered and pressed when the viewer has reacted.</summary>
    public const float PillPressedAlpha = 0.22f, PillPressedHoverAlpha = 0.32f, PillPressedDownAlpha = 0.16f;

    /// <summary>The replies disclosure: the stacked repliers' avatar and overlap, its rung and its chevron.</summary>
    public const float ReplyStackAvatar = 20f, ReplyStackOverlap = -7f, DisclosureSize = 13f, DisclosureLine = 18f, DisclosureChevron = 12f;

    // ── the composer ──

    /// <summary>The composer's text box height.</summary>
    public const float ComposerHeight = 80f;

    /// <summary>The consent label: 12 on an 18 line, up to three lines.</summary>
    public const float ConsentSize = 12f, ConsentLine = 18f;

    /// <summary>The consent label's line cap.</summary>
    public const int ConsentLines = 3;

    // ── the reaction flyout ──

    /// <summary>The palette's columns and its buttons.</summary>
    public const int PaletteColumns = 4;

    /// <summary>A palette button.</summary>
    public const float PaletteButtonWidth = 56f, PaletteButtonHeight = 36f;

    /// <summary>The flyout's size and how far it keeps from the window's edges.</summary>
    public const float FlyoutWidth = 320f, FlyoutHeight = 420f, FlyoutMarginX = 24f, FlyoutMarginY = 32f;

    /// <summary>The flyout's title and error rungs.</summary>
    public const float FlyoutTitleSize = 14f, FlyoutErrorSize = 12f;

    /// <summary>A person of the roster: avatar, name rung and emoji rung.</summary>
    public const float PersonAvatar = 28f, PersonSize = 13f, PersonEmojiSize = 18f;
}

/// <summary>Zune's concert numbers (DIP).</summary>
public static class ZuneConcertRules
{
    // ── focus and shared glyph ──

    /// <summary>The focus visual margin of a tile and a board tile.</summary>
    public const float FocusMargin = 2f;

    /// <summary>The placeholder glyph's alpha over a plate (the tile's and the hero's calendar).</summary>
    public const float GlyphAlpha = 0.72f;

    /// <summary>The accent as a live read (a bind calls it; nothing captures the colour).</summary>
    public static readonly Func<ColorF> AccentRing = static () => R.Ink.AccentAction;

    // ── the event tile (the cover, then three lines whose heights are stated) ──

    /// <summary>The gap between the cover and the words, between the words' lines, and the three lines' sizes and heights.</summary>
    public const float TileGap = 8f, TileTextGap = 2f, TileCaptionSize = 12f, TileCaptionLine = 16f, TileTitleLine = 20f, TilePlaceLine = 18f;

    /// <summary>What the tile adds under its square cover: the gap and the three lines with theirs. The lazy grid's row extra is this plus the row gap.</summary>
    public const float TileChrome = TileGap + TileCaptionLine + TileTextGap + TileTitleLine + TileTextGap + TilePlaceLine;

    /// <summary>The tile cover's decode width, how far the artwork-less tile's plate leans toward the show's accent, and its calendar glyph.</summary>
    public const float TileArtDecode = 320f, TileAccentMix = 0.32f, TileGlyph = 38f;

    /// <summary>The grid's gaps (Z-Sizes-Breakpoints: 16), its row extra and its overscan.</summary>
    public const float GridGap = 16f, GridRowExtra = TileChrome + GridGap;

    /// <summary>The lazy grid's overscan rows.</summary>
    public const int GridOverscanRows = 3;

    // ── the browse-all lead cell ──

    /// <summary>The layered glyphs' alpha and sizes.</summary>
    public const float BrowseAllGlyphAlpha = 0.30f, BrowseAllSmallGlyph = 30f, BrowseAllPinGlyph = 44f, BrowseAllTinyGlyph = 22f;

    // ── headings and the hub's head ──

    /// <summary>A section heading's gap to its count and to its link, the count's lift, and the gap from a heading to its body.</summary>
    public const float HeadGap = 14f, CountLift = 4f, SectionHeadGap = 12f;

    /// <summary>The hub head's lines' gap.</summary>
    public const float HeadLineGap = 4f;

    // ── the location button ──

    /// <summary>The pin's box, the pin and the chevron's box.</summary>
    public const float LocationGlyphBox = 20f, LocationPin = 16f, LocationChevronBoxW = 16f;

    // ── the filter bar's pills and tokens ──

    /// <summary>The pill's height (32; the Compact 40 target is the tier's record), the Compact target, the gap between a pill's parts, its side padding, the glyphs.</summary>
    public const float PillHeight = 32f, CompactTarget = 40f, PillGap = 6f, PillPadX = 12f, PillGlyph = 14f, PillChevron = 10f;

    /// <summary>The group divider's height, the scroller's height and its edge fade.</summary>
    public const float BarDividerH = 20f, BarScrollerH = 44f, BarEdgeFadeBand = 36f;

    /// <summary>The motion of a token's and a pill's reflow, the segment's dock and the chip's exit.</summary>
    public const float TokenReflowMs = 220f, PillReflowMs = 260f, SegmentDockMs = 300f, ChipExitMs = 220f;

    /// <summary>How far the "this weekend" chip travels into the pill (and the segment docks in from), and the segment's entering opacity.</summary>
    public const float ChipTravel = 56f, SegmentDockFrom = 0.4f;

    // ── the fused pill's accent register ──

    /// <summary>The capsule, its raised segment and their paddings.</summary>
    public const float AccentPillH = 32f, AccentPillSegmentH = 26f, AccentPillPadL = 3f, AccentPillPadT = 3f, AccentPillPadR = 12f, AccentPillSegPad = 11f;

    /// <summary>The gaps and sizes inside it.</summary>
    public const float AccentPillGap = 8f, AccentPillSegmentGap = 5f, AccentPillText = 14f, AccentPillCheck = 12f, AccentPillTrailing = 10f;

    // ── the count ticker ──

    /// <summary>The number box and the gap to its word.</summary>
    public const float TickerNumberW = 64f, TickerGap = 2f;

    /// <summary>The ticker's tween length.</summary>
    public const double TickerTweenMs = 380.0;

    // ── the hub feed ──

    /// <summary>The most tiles a shelf shows, the promo card's title lines.</summary>
    public const int ShelfMax = 16, PromoTitleLines = 2;

    /// <summary>The pending silhouette's shelf tiles and grid tiles, and the append tail's shimmering tiles.</summary>
    public const int SeedShelfTiles = 4, SeedGridTiles = 6, SeedTailTiles = 4;

    // ── the flyouts ──

    /// <summary>A row's floor, its glyphs and the gap between rows.</summary>
    public const float FlyoutRowMinH = 40f, FlyoutLead = 16f, FlyoutTrailing = 14f, FlyoutCheck = 14f, FlyoutGap = 2f;

    /// <summary>The when and where flyouts' widths.</summary>
    public const float WhenFlyoutW = 320f, WhereFlyoutW = 264f;

    /// <summary>The month leaf: its head, the back target, its foot and its height cap.</summary>
    public const float MonthHeadMinH = 36f, MonthBackBox = 28f, MonthFootMinH = 44f, MonthMaxH = 400f;

    /// <summary>The calendar's cells.</summary>
    public const float CalendarCellW = 38f, CalendarCellH = 32f, CalendarHeadH = 20f, CalendarGap = 4f;

    // ── the popups' fit (a Compact window is 300 wide) ──

    /// <summary>How far a flyout keeps from the window's side edges (both sides), and the flyout's own padding (<c>Spacing.S</c>) the calendar sits inside.</summary>
    public const float FlyoutMarginX = 12f, FlyoutPad = 8f;

    /// <summary>A flyout's width: its natural one, less than the window less the margins where the window is narrower.</summary>
    public static float FlyoutWidthIn(float natural, float viewportWidth)
        => MathF.Min(natural, MathF.Max(1f, viewportWidth - 2f * FlyoutMarginX));

    /// <summary>How far a flyout keeps from the window's top and bottom (both), and what the place picker's card holds besides its results (the field, the
    /// location button, the padding and the gaps).</summary>
    public const float FlyoutMarginY = 24f, PickerChrome = 160f;

    /// <summary>A flyout's height cap: its natural one, or the window less its margins where the window is shorter (a Short window is 330 tall).</summary>
    public static float FlyoutHeightIn(float natural, float viewportHeight)
        => MathF.Min(natural, MathF.Max(1f, viewportHeight - 2f * FlyoutMarginY));

    /// <summary>The place picker's results cap in a window <paramref name="viewportHeight"/> tall: 248, or what the card leaves (a row at least).</summary>
    public static float PickerResultsIn(float viewportHeight)
        => MathF.Max(PickerRowH, MathF.Min(PickerResultsMaxH, viewportHeight - PickerChrome - 2f * FlyoutMarginY));

    /// <summary>A calendar day's width in a flyout of <paramref name="flyoutWidth"/>: 38, or the seven shared across what the padding leaves.</summary>
    public static float CalendarCellWIn(float flyoutWidth)
        => MathF.Min(CalendarCellW, MathF.Max(1f, (flyoutWidth - 2f * FlyoutPad - 6f * CalendarGap) / 7f));

    /// <summary>A calendar day's height: 32, or the tier's hit edge where that is taller (40 at Compact).</summary>
    public static float CalendarCellHIn(float target) => MathF.Max(CalendarCellH, target);

    /// <summary>The month leaf's back target: 28, or the tier's hit edge where that is larger.</summary>
    public static float BackEdge(float target) => MathF.Max(MonthBackBox, target);

    /// <summary>The place picker's field in a card of <paramref name="card"/> wide (the field is 20 narrower than the card).</summary>
    public static float PickerFieldIn(float card) => MathF.Max(1f, card - (PickerW - PickerFieldW));

    // ── the location picker ──

    /// <summary>The picker's width, its field, its results' cap, a row and its pin.</summary>
    public const float PickerW = 360f, PickerFieldW = 340f, PickerFieldH = 32f, PickerResultsMaxH = 248f, PickerRowH = 48f, PickerPin = 18f;

    // ── the sub-page frame (the schedule and the event page) ──

    /// <summary>The page's air under the band and past the player dock's reserve.</summary>
    public const float PageTop = 8f, PageBottom = 24f;

    // ── the event page ──

    /// <summary>The title's line cap (it wraps by words at every tier; the identity column grows to hug it).</summary>
    public const int TitleLines = 2;

    /// <summary>The gap under the numeral (stacked), beside the numeral and its month (Compact), the lift of the words beside it and of the facts.</summary>
    public const float NumeralGap = 2f, NumeralBesideGap = 16f, NumeralBesideLift = 6f, FactsLift = 6f;

    /// <summary>The seed date the skeleton's numeral stands on (the exact silhouette of the loaded page).</summary>
    public static readonly DateTimeOffset SeedDate = new(2030, 1, 1, 20, 0, 0, TimeSpan.Zero);

    /// <summary>The tickets slab: the gap between its rows, an offer's air, the footnote (12 on 16, 8 above) and the extra under the last row.</summary>
    public const float SlabGap = 4f, OfferPadY = 4f, NoteSize = 12f, NoteLine = 16f, NoteTop = 8f, SlabPadBottomExtra = 2f;

    /// <summary>The lineup: the gap between faces in a line and the chevron of a row.</summary>
    public const float LineupGap = 32f, ActChevron = 14f;

    /// <summary>The related card's side padding.</summary>
    public const float CardPadX = 16f;

    // ── the tour dashboard ──

    /// <summary>The empty month card's air and the location button's width beside "view event".</summary>
    public const float EmptyPadY = 48f, ScheduleLocationW = 220f;

    /// <summary>The month tabs' height, the board's head floor, the chevron and the pin.</summary>
    public const float MonthTabsH = 48f, BoardHeadMinH = 52f, BoardChevron = 14f, BoardPin = 12f;

    /// <summary>A board tile's floor, its rail, its text gap, its trailing glyph, the day column (a run's is wider), and the gap between the wide arm's columns.</summary>
    public const float BoardTileMinH = 64f, BoardRailW = 3f, BoardRailH = 32f, BoardTextGap = 2f, BoardTrailing = 16f, BoardDayW = 48f, BoardRunDayW = 64f, BoardColumnGap = 12f;

    /// <summary>The photo's vertical focus, decode width, reveal fade and calendar glyph.</summary>
    public const float HeroFocusY = 0.30f, HeroDecode = 1024f, HeroRevealMs = 220f, HeroGlyph = 52f;

    // ── the shy month pill ──

    /// <summary>The pill's height and its motion.</summary>
    public const float PillPresenceH = 32f, PillPresenceMs = 200f, PillExitMs = 260f, PillEnterDy = 8f, PillExitDy = 6f;

    /// <summary>The offset's write floor (DIP), the sub-half-DIP noise it ignores and how far past the viewport top the board header may be and still count as crossed.</summary>
    public const float ScrollStepDip = 8f, PillStepNoise = 0.5f, PillCrossSlack = 4f;

    /// <summary>How long the pill lingers past the last step.</summary>
    public const float LingerMs = 1000f;
}

/// <summary>Zune's concert records per content tier (see the file header).</summary>
public static class ZuneConcertTiers
{
    /// <summary>Compact (Z-Concert-Compact): the title a 64/72 line that bleeds off the edge or fits it, the numeral 64 beside its month, the facts
    /// under them, the slab stacked and bled to the gutters, the lineup and the related concerts as 64 and 56 rows.</summary>
    public static ZuneConcertLayout Compact { get; } = new()
    {
        Tier = WidthTier.Compact, TitleSize = 64f, TitleLine = 72f, TitleReserveLines = ZuneConcertRules.TitleLines, NumeralSize = 64f, NumeralLine = 64f,
        DateStacked = false, DateGap = 12f, DateTop = 6f, DateWordsSize = 15f, DateWordsLine = 20f, FactsSize = 15f, FactsLine = 22f, FactsGap = 4f,
        IdentityGap = 6f, SectionGap = 20f, HeroGap = 0f, SlabTop = 20f, TicketsW = 0f, SlabBleed = true, BuyButton = true, SlabPadX = 0f, SlabPadY = 14f,
        SlabHeadSize = 26f, SlabHeadLine = 32f, OfferRowH = 48f, OfferSize = 15f, LineupAvatar = 48f, LineupRows = true, LineupRowH = 64f,
        RelatedColumns = 1, RelatedPlate = false, RelatedRowH = 56f, RelatedGap = 0f, RelatedDateW = 40f, RelatedDaySize = 28f, RelatedDayLine = 30f,
        Target = 40f, HubPill = 40f, GridMinColumn = 150f, ZoneGap = 24f, ScheduleWide = false, ScheduleDateW = 48f, SchedulePhotoW = 0f,
        SchedulePhotoH = 160f, ScheduleTitleSize = 40f, ScheduleTitleLine = 48f, LinkTarget = 40f,
    };

    /// <summary>Small (Z-Concert-Small): the title 40/48, the numeral 64 over its words, the 260 slab beside the facts, the rows.</summary>
    public static ZuneConcertLayout Small { get; } = Compact with
    {
        Tier = WidthTier.Small, TitleSize = 40f, TitleLine = 48f, DateStacked = true, DateGap = 28f, DateTop = 4f,
        DateWordsSize = 13f, DateWordsLine = 18f, HeroGap = 32f, TicketsW = 260f, SlabBleed = false, BuyButton = false, SlabPadX = 20f, SlabHeadSize = 22f, SlabHeadLine = 28f,
        OfferRowH = 36f, OfferSize = 14f, LineupRowH = 56f, RelatedGap = 0f, Target = 36f, HubPill = 32f, GridMinColumn = 160f, ScheduleDateW = 48f,
        SchedulePhotoH = 180f, LinkTarget = 0f,
    };

    /// <summary>Standard (Z-Concert): the title 64/72, the numeral 64 over "november · saturday", the 320 slab beside the facts, a line of round faces
    /// and the related concerts as four plate cards across.</summary>
    public static ZuneConcertLayout Standard { get; } = Small with
    {
        Tier = WidthTier.Standard, TitleSize = 64f, TitleLine = 72f, TitleReserveLines = 1, DateGap = 40f, DateTop = 8f, DateWordsSize = 14f, DateWordsLine = 20f, FactsGap = 6f,
        IdentityGap = 10f, SectionGap = 32f, HeroGap = 56f, TicketsW = 320f, SlabPadX = 24f, SlabPadY = 20f, SlabHeadSize = 26f, SlabHeadLine = 32f,
        OfferRowH = 40f, OfferSize = 15f, LineupAvatar = 64f, LineupRows = false, LineupRowH = 64f, RelatedColumns = 4, RelatedPlate = true, RelatedRowH = 72f,
        RelatedGap = 16f, RelatedDateW = 44f, RelatedDaySize = 32f, RelatedDayLine = 32f, GridMinColumn = 200f, ZoneGap = 32f, ScheduleWide = true,
        ScheduleDateW = 56f, SchedulePhotoW = 280f, SchedulePhotoH = 200f, ScheduleTitleSize = 64f, ScheduleTitleLine = 72f,
    };

    /// <summary>Huge (Z-Concert-Huge): the title and the numeral at 150, the sentences at 22/30, the 440 slab.</summary>
    public static ZuneConcertLayout Huge { get; } = Standard with
    {
        Tier = WidthTier.Huge, TitleSize = 150f, TitleLine = 160f, NumeralSize = 150f, NumeralLine = 150f, DateGap = 64f, DateWordsSize = 17f, DateWordsLine = 22f,
        FactsSize = 22f, FactsLine = 30f, FactsGap = 10f, SectionGap = 44f, HeroGap = 64f, TicketsW = 440f, SlabPadX = 28f, SlabPadY = 22f,
        OfferRowH = 48f, LineupAvatar = 80f, ZoneGap = 44f, SchedulePhotoW = 360f, SchedulePhotoH = 240f,
    };

    /// <summary>Huge 4K: Huge's composition with the 480 slab and the 220 grid columns.</summary>
    public static ZuneConcertLayout Huge4K { get; } = Huge with
    {
        Tier = WidthTier.Huge4K, TicketsW = 480f, OfferRowH = 52f, GridMinColumn = 220f, Target = 40f, SchedulePhotoW = 420f, SchedulePhotoH = 280f,
    };

    /// <summary>The record of content tier <paramref name="tier"/>.</summary>
    public static ZuneConcertLayout For(WidthTier tier) => tier switch
    {
        WidthTier.Compact => Compact,
        WidthTier.Small => Small,
        WidthTier.Standard => Standard,
        WidthTier.Huge => Huge,
        _ => Huge4K,
    };

    /// <summary>The record of <paramref name="facts"/> by the CONTENT tier (a docked panel leaves the page narrower than the window); Standard's where no
    /// frame resolved the tiers (a view mounted alone).</summary>
    public static ZuneConcertLayout Of(in TierFacts facts)
    {
        if (facts.Policy is null) return Standard;
        return facts.Height == HeightTier.Short ? s_short[Math.Clamp((int)facts.Content, 0, s_short.Length - 1)] : For(facts.Content);
    }

    /// <summary>The Short rung (windows 330-699 tall: the band folds to 52 and the player bar holds 72, so about 160 of height is left): the SPACE
    /// closes (the gaps, the schedule's photo); the TYPE does not. The title, the numeral, the date words, the facts and the schedule's name keep the
    /// tier's own step (Zune is a large typeface: it wraps and the page scrolls, it is never made smaller to fit). The columns, the slab and the rows are
    /// the content tier's own. A pure fold of the tier's record.</summary>
    static ZuneConcertLayout Fold(ZuneConcertLayout r) => r with
    {
        SectionGap = MathF.Min(r.SectionGap, ShortGap), HeroGap = MathF.Min(r.HeroGap, Standard.HeroGap), ZoneGap = MathF.Min(r.ZoneGap, ShortGap),
        SchedulePhotoH = MathF.Min(r.SchedulePhotoH, ShortPhotoH),
    };

    /// <summary>The folded gaps and the folded schedule photo's height.</summary>
    public const float ShortGap = 20f, ShortPhotoH = 140f;

    static readonly ZuneConcertLayout[] s_short = [Fold(Compact), Fold(Small), Fold(Standard), Fold(Huge), Fold(Huge4K)];
}

/// <summary>What the concert pages lay out in one content tier (see <see cref="ZuneConcertTiers"/>).</summary>
public sealed record ZuneConcertLayout : ITierLayoutRecord
{
    /// <summary>The content tier.</summary>
    public required WidthTier Tier { get; init; }

    /// <summary>None: every region of the page fits at every size its tier lays out (the columns yield, the slab stacks).</summary>
    public ImmutableArray<TierRegion> Regions => ImmutableArray<TierRegion>.Empty;

    /// <summary>The title's size and line: the title keeps this step at every height and wraps over <see cref="ZuneConcertRules.TitleLines"/> lines.</summary>
    public required float TitleSize { get; init; }

    /// <summary>See <see cref="TitleSize"/>.</summary>
    public required float TitleLine { get; init; }

    /// <summary>The title lines the identity holds whatever the title (audit B7, V30): the skeleton does not know the words, so the page reserves the
    /// lines a concert's name commonly takes at this tier, on the skeleton and loaded alike, and the title landing moves nothing under it. Compact's
    /// and Small's narrow identity wrap most names (two); the wider tiers hold one. A longer name still grows the identity.</summary>
    public int TitleReserveLines { get; init; } = 1;

    /// <summary>The date numeral's size and line.</summary>
    public required float NumeralSize { get; init; }

    /// <summary>See <see cref="NumeralSize"/>.</summary>
    public required float NumeralLine { get; init; }

    /// <summary>The numeral stands over its month and weekday and the facts stand beside it; false (Compact): the month beside the numeral and the
    /// facts under them.</summary>
    public required bool DateStacked { get; init; }

    /// <summary>The gap between the date block and the facts (beside: the column gap; under: the row gap), and the air above the block.</summary>
    public required float DateGap { get; init; }

    /// <summary>See <see cref="DateGap"/>.</summary>
    public required float DateTop { get; init; }

    /// <summary>The month and weekday words' size and line.</summary>
    public required float DateWordsSize { get; init; }

    /// <summary>See <see cref="DateWordsSize"/>.</summary>
    public required float DateWordsLine { get; init; }

    /// <summary>The facts' sentence size, line and the gap between two.</summary>
    public required float FactsSize { get; init; }

    /// <summary>See <see cref="FactsSize"/>.</summary>
    public required float FactsLine { get; init; }

    /// <summary>See <see cref="FactsSize"/>.</summary>
    public required float FactsGap { get; init; }

    /// <summary>The gap between the caption, the title and the date block.</summary>
    public required float IdentityGap { get; init; }

    /// <summary>The gap between the page's sections (hero, lineup, related).</summary>
    public required float SectionGap { get; init; }

    /// <summary>The gap between the identity and the slab beside it (and between the schedule's copy and its photo).</summary>
    public required float HeroGap { get; init; }

    /// <summary>The gap above a stacked slab.</summary>
    public required float SlabTop { get; init; }

    /// <summary>The slab's width beside the facts; 0 stacks it under them.</summary>
    public required float TicketsW { get; init; }

    /// <summary>The stacked slab bleeds to the gutters (Compact).</summary>
    public required bool SlabBleed { get; init; }

    /// <summary>The slab closes with one full-width accent "buy tickets" button (Z-Concert-Compact) where an offer can be bought online; the offer rows keep their
    /// own rings, so the choice of provider stays reachable.</summary>
    public bool BuyButton { get; init; }

    /// <summary>The slab's side and vertical padding.</summary>
    public required float SlabPadX { get; init; }

    /// <summary>See <see cref="SlabPadX"/>.</summary>
    public required float SlabPadY { get; init; }

    /// <summary>The slab heading's size and line.</summary>
    public required float SlabHeadSize { get; init; }

    /// <summary>See <see cref="SlabHeadSize"/>.</summary>
    public required float SlabHeadLine { get; init; }

    /// <summary>An offer row's floor and its provider's size.</summary>
    public required float OfferRowH { get; init; }

    /// <summary>See <see cref="OfferRowH"/>.</summary>
    public required float OfferSize { get; init; }

    /// <summary>The lineup's face, whether the lineup is rows with a chevron, and a row's height.</summary>
    public required float LineupAvatar { get; init; }

    /// <summary>See <see cref="LineupAvatar"/>.</summary>
    public required bool LineupRows { get; init; }

    /// <summary>See <see cref="LineupAvatar"/>.</summary>
    public required float LineupRowH { get; init; }

    /// <summary>The related concerts' columns, whether a card wears the plate, its height, the gap between cards and its date column and numeral.</summary>
    public required int RelatedColumns { get; init; }

    /// <summary>See <see cref="RelatedColumns"/>.</summary>
    public required bool RelatedPlate { get; init; }

    /// <summary>See <see cref="RelatedColumns"/>.</summary>
    public required float RelatedRowH { get; init; }

    /// <summary>See <see cref="RelatedColumns"/>.</summary>
    public required float RelatedGap { get; init; }

    /// <summary>See <see cref="RelatedColumns"/>.</summary>
    public required float RelatedDateW { get; init; }

    /// <summary>See <see cref="RelatedColumns"/>.</summary>
    public required float RelatedDaySize { get; init; }

    /// <summary>See <see cref="RelatedColumns"/>.</summary>
    public required float RelatedDayLine { get; init; }

    /// <summary>A command's hit edge (40 at Compact and 4K).</summary>
    public required float Target { get; init; }

    /// <summary>The hub's filter pills and tokens (where, when, the genres): the 40 target at Compact, 32 elsewhere.</summary>
    public required float HubPill { get; init; }

    /// <summary>The "all events" grid's minimum column, and the gap between the hub's and the schedule's zones.</summary>
    public required float GridMinColumn { get; init; }

    /// <summary>See <see cref="GridMinColumn"/>.</summary>
    public required float ZoneGap { get; init; }

    /// <summary>The schedule's wide arm: the photo beside the copy and two balanced columns in a month board.</summary>
    public required bool ScheduleWide { get; init; }

    /// <summary>The schedule's next-show date column, its photo (width beside the copy, height) and its name's size and line.</summary>
    public required float ScheduleDateW { get; init; }

    /// <summary>See <see cref="ScheduleDateW"/>.</summary>
    public required float SchedulePhotoW { get; init; }

    /// <summary>See <see cref="ScheduleDateW"/>.</summary>
    public required float SchedulePhotoH { get; init; }

    /// <summary>See <see cref="ScheduleDateW"/>.</summary>
    public required float ScheduleTitleSize { get; init; }

    /// <summary>See <see cref="ScheduleDateW"/>.</summary>
    public required float ScheduleTitleLine { get; init; }

    /// <summary>A bare link word's minimum hit edge (the "near you" link beside a heading): 40 at Compact, 0 elsewhere (the word is its own target).</summary>
    public float LinkTarget { get; init; }

    /// <summary>The hub's records are the event page's (the hub has no board of its own): its filter pills' height in the tiers <paramref name="facts"/>.</summary>
    public static float HubPillOf(in TierFacts facts) => ZuneConcertTiers.Of(in facts).HubPill;
}
