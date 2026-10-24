// ── Themes/Classic/Structure/Rules/ClassicShowRules.cs — the numbers of Classic's show page (themes plan 6.1, T9c) ────────────────────
//
// The show reader's pictures (`Views\Show\*`) own these sizes; the arithmetic the list's seeds and the skeleton must agree on row for row is the
// core's (`ShowReaderRules`: the rail, header and month extents, the gutters, the strip's column, the head's column counts) and is NOT repeated
// here. What is here is what only the picture decides: the section rhythm, the hero and up-next cards, the toolbar's controls and the type of the
// reader's small texts (the prototype's .sechead, .door, .resume, .mini, .wordrail).

namespace Wavee.Themes.Classic;

/// <summary>Classic's show page geometry (DIP).</summary>
internal static class ClassicShowRules
{
    /// <summary>The Display face of the reader's headings, numerals and the hero's minutes.</summary>
    public const string DisplayFace = "Segoe UI Variable Display";

    /// <summary>The reader's voice: Classic's semibold, the weight of every other Classic page's heads (owner, 2026-10-24: semibold sentence case;
    /// the light 300 lowercase heads were Zune's voice). It sets the section heads, "Nothing matches", the month headers and the continue hero's
    /// minutes; the words are the loc values as written (sentence case).</summary>
    public const ushort VoiceWeight = 600;

    // ── the section rhythm ──

    /// <summary>The head's air above, and the gap between its sections.</summary>
    public const float HeadTop = 12f, SectionGap = 26f;

    /// <summary>A section head's pivot (Display 24 over the core's 30-DIP line, <see cref="VoiceWeight"/>).</summary>
    public const float PivotSize = 24f;

    /// <summary>A section head's sub line: its lift off the baseline, and the gap stacked under the pivot at a narrow reader.</summary>
    public const float SubLineLift = 4f, StackedGap = 2f;

    /// <summary>The reading measure of the about and of the rail's publisher line when the rail states none.</summary>
    public const float AboutMeasure = 620f, AboutSize = 14f;

    /// <summary>A quiet line (the unavailable head, the empty show, the empty arm's hint) and the failed card's wrapped line.</summary>
    public const float QuietSize = 14f, QuietLineHeight = 20f, FailedLineHeight = 19f;

    /// <summary>"Nothing matches" (Display 20/26) and a month header's word (Display 18), both in <see cref="VoiceWeight"/>.</summary>
    public const float NothingSize = 20f, NothingLineHeight = 26f, GroupSize = 18f;

    /// <summary>The pinned month's plate and one year's row in the strip.</summary>
    public const float StickyMonthPlateH = 20f, YearRowHeight = 15f;

    /// <summary>The cold skeleton's seed rows.</summary>
    public const int SeedRows = 8;

    // ── the toolbar ──

    /// <summary>The toolbar's body (the core's plane is <c>ShowReaderRules.RailExtent</c>, the body centres in it), a glyph control's edge (the stage rule's own
    /// <c>ShowToolbarLayout.Glyph</c>: it sums the controls' widths) and glyph, the hairline between find and sort, the folded filter's chevron, the find box's floor and its open-field width.</summary>
    public const float ToolbarHeight = 40f, ControlEdge = ShowToolbarLayout.Glyph, ToggleGlyph = 14f, DividerHeight = 16f, FilterChevron = 10f,
                       FindMinHeight = 32f, FindFillMax = 9999f;

    /// <summary>A glyph control's edge under the reader's narrow arm: the Compact 40 target (C-Show-Compact draws its find and sort 40 x 40). A 30 glyph's
    /// slop met the list's scroll bar lane at the row's end (MultiSelect 39 x 40). The style states it (<c>ShowReaderStyle.NarrowToolbarGlyph</c>), so the
    /// stage rule sums 40 there too.</summary>
    public const float NarrowControlEdge = ClassicDetailTiers.CompactTarget;

    // ── the visit head ──

    /// <summary>The head's shimmer: its title bar's width and the hero card's height.</summary>
    public const float HeadSeedTitleWidth = 160f, HeroSeedHeight = 124f;

    /// <summary>The continue hero: art (wide, narrow), the progress track's longest run and height, the card's padding, its title (17/22),
    /// the minutes (Display 30/34 in <see cref="VoiceWeight"/>) and the "left of" words.</summary>
    public const float HeroArt = 96f, HeroArtNarrow = 64f, HeroTrackMax = 420f, HeroRuleHeight = 4f, CardPad = 14f,
                       HeroTitleSize = 17f, HeroTitleLine = 22f, HeroLeftDisplay = 30f, HeroLeftDisplayLine = 34f, HeroLeftSize = 12.5f;

    /// <summary>The up-next card: art, the numeral column (Display 22/28), the plate's gap and the seed's figure-space runs.</summary>
    public const float MiniArt = 40f, MiniNumeral = 34f, MiniNumeralSize = 22f, MiniNumeralLine = 28f, MiniGap = 10f;

    /// <summary>The seed's U+2007 runs that make the derived shimmer draw a title bar and a meta bar.</summary>
    public const int MiniSeedTitleRun = 18, MiniSeedMetaRun = 10;

    // ── the rail's rating row ──

    /// <summary>The rating button's gap and star.</summary>
    public const float RatingGap = 6f, RatingStar = 13f;
}
