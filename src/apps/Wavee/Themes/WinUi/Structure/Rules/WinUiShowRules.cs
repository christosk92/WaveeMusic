// ── Themes/WinUi/Structure/Rules/WinUiShowRules.cs — the numbers of WinUI's show reader (themes plan 6.2; W6b, winui menus/podcasts) ─────────────
//
// The show reader's pictures (`Views\Show\WinUiShow*.cs`) own these sizes; the arithmetic the list's seeds and the skeleton must agree on row for row is the
// core's (`ShowReaderRules`: the rail, header and month extents, the gutters, the strip's column, the head's column counts) and is NOT repeated here.
// Boards: W-Show (and -Compact, -Small, -Huge). WinUI's type ramp: the section heads are Subtitle (20/28 semibold, the Display face), a month header is
// Body Strong (14/20 semibold), the small counts and metas Caption (12/16); the accent, never the show's cover tone, marks progress and the primary.

namespace Wavee.Themes.WinUi;

/// <summary>WinUI's show reader geometry (DIP).</summary>
internal static class WinUiShowRules
{
    /// <summary>The Display face of the reader's section heads (WinUI's Subtitle and Title use Segoe UI Variable Display).</summary>
    public const string DisplayFace = "Segoe UI Variable Display";

    /// <summary>WinUI's heading weight (Subtitle, Body Strong: semibold).</summary>
    public const ushort VoiceWeight = 600;

    // ── the section rhythm ──

    /// <summary>The head's air above, and the gap between its sections.</summary>
    public const float HeadTop = 12f, SectionGap = 24f;

    /// <summary>A section head (Subtitle 20 over the core's 30-DIP line).</summary>
    public const float PivotSize = 20f;

    /// <summary>A section head's sub line: its lift off the baseline, and the gap stacked under the pivot at a narrow reader.</summary>
    public const float SubLineLift = 4f, StackedGap = 2f;

    /// <summary>The reading measure of the about and of the rail's publisher line when the rail states none; the about's Body 14.</summary>
    public const float AboutMeasure = 620f, AboutSize = 14f;

    /// <summary>A quiet line (the unavailable head, the empty show, the empty arm's hint) and the failed card's wrapped line: Body 14/20.</summary>
    public const float QuietSize = 14f, QuietLineHeight = 20f, FailedLineHeight = 20f;

    /// <summary>"Nothing matches" (Subtitle 20/28) and a month header (Body Strong 14, W-Show "September 2026").</summary>
    public const float NothingSize = 20f, NothingLineHeight = 28f, GroupSize = 14f;

    /// <summary>The pinned month's plate and one year's row in the strip.</summary>
    public const float StickyMonthPlateH = 20f, YearRowHeight = 16f;

    /// <summary>The pinned month's plate padding (Caption on a card plate) and the strip row's corner.</summary>
    public const float StickyPadX = 8f, StickyPadY = 2f, YearCorner = 4f;

    /// <summary>The cold skeleton's seed rows.</summary>
    public const int SeedRows = 8;

    // ── the toolbar ──

    /// <summary>The toolbar's body (the core's plane is <c>ShowReaderRules.RailExtent</c>, the body centres in it), a glyph control's edge (the stage rule's
    /// own <c>ShowToolbarLayout.Glyph</c>), its glyph (Fluent 16), the find box's floor and its open-field width.</summary>
    public const float ToolbarHeight = 40f, ControlEdge = ShowToolbarLayout.Glyph, ToggleGlyph = 16f, FindMinHeight = 32f, FindFillMax = 9999f;

    /// <summary>A glyph control's edge under the reader's narrow arm: the Compact 40 target (W-Show-Compact).</summary>
    public const float NarrowControlEdge = WinUiDetailRules.CompactTarget;

    // ── the visit head ──

    /// <summary>The head's shimmer: its title bar's width and the hero card's height.</summary>
    public const float HeadSeedTitleWidth = 160f, HeroSeedHeight = 112f;

    /// <summary>The continue card: art (wide, narrow), the progress bar's longest run and height (WinUI's ProgressBar, 3), the card's padding (16), its
    /// title (Body Strong 14/20), the minutes (Subtitle 20/28) and the "left of" words (Caption 12).</summary>
    public const float HeroArt = 80f, HeroArtNarrow = 56f, HeroTrackMax = 420f, HeroRuleHeight = 3f, CardPad = 16f,
                       HeroTitleSize = 14f, HeroTitleLine = 20f, HeroLeftDisplay = 20f, HeroLeftDisplayLine = 28f, HeroLeftSize = 12f;

    /// <summary>A cover's corner (the WinUI card's inner 4) and the progress bar's.</summary>
    public const float ArtCorner = 4f, RuleCorner = 1.5f;

    /// <summary>The up-next card: art, the numeral column (Subtitle 20/28, tertiary), the plate's gap and padding.</summary>
    public const float MiniArt = 40f, MiniNumeral = 32f, MiniNumeralSize = 20f, MiniNumeralLine = 28f, MiniGap = 12f, MiniPadX = 12f, MiniPadY = 8f;

    /// <summary>The seed's U+2007 runs that make the derived shimmer draw a title bar and a meta bar.</summary>
    public const int MiniSeedTitleRun = 18, MiniSeedMetaRun = 10;

    // ── the rail's rows ──

    /// <summary>The rating button's gap, star and padding.</summary>
    public const float RatingGap = 6f, RatingStar = 12f, RatingPadX = 4f, RatingPadY = 2f;
}
