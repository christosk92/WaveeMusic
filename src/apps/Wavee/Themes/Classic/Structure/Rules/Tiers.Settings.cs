// ── Themes/Classic/Structure/Rules/Tiers.Settings.cs — Classic's Settings page (and its theme-options rows) per content tier (themes plan 3.6, TC8) ──────
//
// ONE record per CONTENT tier (`ctx.Tier().Content`: the region the page lives in, which is what the page gutter follows). A view reads
// `ClassicSettingsTiers.Of(ctx.Tier())` and never compares a width with a number (WVTH013). Compact follows `C-Settings-Compact`,
// `C-SettingsSections-Compact` and `C-ThemeOptions-Compact`, Small the -Small boards, Huge the -Huge boards; Standard is the tuned design (P3b) and its record is
// exactly the numbers the page held before tiers (`ClassicSettingsRules`: the 1000-DIP left-aligned column, the 208 x 172 theme cards that wrap).
//
//   tier      tabs                              rows                                       theme picker                         columns
//   Compact   44 tall, scroll, full-bleed fade  control UNDER the label (picker 40);       2-up grid, 72 thumbs, no text        1, fill
//                                               toggles keep label | state | 48 x 40
//   Small     40 tall, scroll, fade at the edge label | control (32 controls)              one strip, 190 cards, 72 thumbs      1, 1000
//   Standard  stock item, in the gutter            label | control                            208 x 172 cards that wrap            1, 1000
//   Huge      stock item, in the gutter            label | control                            184 x 172 cards that wrap (5-up)     2 x 1000, gap 32, left-aligned
//
// COLUMNS. At Huge a tab's sections split over two columns (the board: Appearance puts Theme, options, mode, zoom and marquee on the left and Lists, Sidebar, Lyrics
// and Full screen on the right; Playback puts Runtime, Audio and Sound beside Player bar and Video). `SplitAt` picks the section boundary that balances the two: pure
// arithmetic over the sections' weights, never a width comparison. A tab with one section stays one column. The columns never exceed 1000 each and the pair is
// LEFT-ALIGNED (never centred), as the Huge boards draw.
//
// THE HEAD AND THE SECTION HEADERS. The Compact, Small and Huge boards draw the page title behind a 24 gear glyph (gap 12); the tuned Standard title stays alone. A
// section header is the glyph and the title with its caption UNDER it at Compact (C-SettingsSections-Compact: 20 above, 6 under) and at Standard (the tuned 32 / 8);
// at Small and Huge the caption rides the title's line (C-SettingsSections-Small: baseline row, gap 8, 24 / 8, the caption one line with an ellipsis;
// C-SettingsSections-Huge: 28 / 4). The views key the glyph, the title and each header and animate the change: the glyph fades, the title slides, a header's
// height reveals (the cards below ride it) and its caption slides between the two slots.
//
//   tier      title glyph   header            above / under
//   Compact   24, gap 12    caption under     20 / 6
//   Small     24, gap 12    one row           24 / 8
//   Standard  none          caption under     32 / 8   (tuned)
//   Huge      24, gap 12    one row           28 / 4
//
// TOUCH. `Target` is the Compact hit target (40): the page asks for a control at that size and the record never decides what a control is. The stock ComboBox takes
// its Large (40-DIP) field and fills the stacked row, and a NumberBox takes a 40-DIP field through its options' style (`ClassicSettings.Touch.cs`).
//
// Public, so `Wavee.Tests` pins the records (the assembly has no InternalsVisibleTo).

using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>How the theme picker's cards are laid out in a tier.</summary>
public enum ClassicThemePickerForm : byte
{
    /// <summary>The tuned strip of fixed 208 x 172 cards that wraps (Standard) or 184 x 172 (Huge's five-up).</summary>
    Wrap,
    /// <summary>Compact: a two-column grid of fluid cards (72 thumb over the name), no description.</summary>
    Grid,
    /// <summary>Small: one horizontal strip of 190-wide cards that scrolls.</summary>
    Strip,
}

/// <summary>What the Settings page lays out in one content tier.</summary>
/// <param name="Tier">The content tier.</param>
/// <param name="Target">The hit target of the page's controls (Compact 40; 0 keeps each control's own size).</param>
/// <param name="StacksControls">A row's control sits UNDER its label (full width), except a toggle, which keeps the label | state | switch row.</param>
/// <param name="TabHeight">The tab strip's item height (0 keeps the stock item).</param>
/// <param name="TabsScroll">The tab strip scrolls sideways (a lane that runs off the edge fades out) instead of ending at the column.</param>
/// <param name="TabsBleed">The strip and its rule run edge to edge of the page card (Compact), its first label still on the gutter.</param>
/// <param name="Columns">The tab body's columns (Huge 2).</param>
/// <param name="ColumnMaxW">One column's cap (the board's 1000).</param>
/// <param name="ColumnGap">The gap between columns.</param>
/// <param name="Picker">The theme picker's form.</param>
/// <param name="PickerCardW">A picker card's width (0: fluid, from the grid).</param>
/// <param name="PickerCardH">A picker card's height (0: sized by its content).</param>
/// <param name="PickerThumbH">The thumbnail's height.</param>
/// <param name="PickerShowsText">The card carries the theme's description under its name.</param>
/// <param name="PickerGap">The gap between picker cards.</param>
/// <param name="PickerPad">The padding of the card the picker sits in (the board's 12 at Compact, 16 elsewhere).</param>
/// <param name="TitleGlyph">The page title stands behind the 24 gear glyph (Compact, Small, Huge boards; the tuned Standard title stands alone).</param>
/// <param name="SectionInline">A section header's caption rides the title's line (Small, Huge) instead of hanging under it.</param>
/// <param name="SectionTop">The space above a section header (none above the tab's first).</param>
/// <param name="SectionBottom">The space between a section header and its first card.</param>
public sealed record ClassicSettingsLayout(
    WidthTier Tier, float Target, bool StacksControls, float TabHeight, bool TabsScroll, bool TabsBleed,
    int Columns, float ColumnMaxW, float ColumnGap,
    ClassicThemePickerForm Picker, float PickerCardW, float PickerCardH, float PickerThumbH, bool PickerShowsText, float PickerGap, float PickerPad,
    bool TitleGlyph, bool SectionInline, float SectionTop, float SectionBottom)
{
    /// <summary>The page lays its controls out for touch (the Compact rule: every hit target is at least 40).</summary>
    public bool Touch => Target > 0f;

    /// <summary>The toggle's own cell: 48 x 40 at a hit target, the engine's compact switch (36 tall) otherwise. 0 is the engine's width.</summary>
    public float ToggleMinWidth => Touch ? 48f : 0f;

    /// <summary>The toggle's least height.</summary>
    public float ToggleMinHeight => Touch ? Target : 36f;
}

/// <summary>Classic's Settings tier records.</summary>
public static class ClassicSettingsTiers
{
    private static readonly ClassicSettingsLayout[] s_records =
    [
        // Compact (C-Settings-Compact): the tabs bleed to the card's edges, 44 tall; the rows stack; the theme picker is a 2-up grid of 72-high thumbs.
        new(WidthTier.Compact, Target: 40f, StacksControls: true, TabHeight: 44f, TabsScroll: true, TabsBleed: true,
            Columns: 1, ColumnMaxW: 1000f, ColumnGap: 0f,
            ClassicThemePickerForm.Grid, PickerCardW: 0f, PickerCardH: 0f, PickerThumbH: 72f, PickerShowsText: false, PickerGap: 12f, PickerPad: 12f,
            TitleGlyph: true, SectionInline: false, SectionTop: 20f, SectionBottom: 6f),
        // Small (C-Settings-Small): 40-tall tabs that fade out at the edge; the picker is one scrolling strip of 190-wide cards with 72-high thumbs.
        new(WidthTier.Small, Target: 0f, StacksControls: false, TabHeight: 40f, TabsScroll: true, TabsBleed: true,
            Columns: 1, ColumnMaxW: 1000f, ColumnGap: 0f,
            ClassicThemePickerForm.Strip, PickerCardW: 190f, PickerCardH: 0f, PickerThumbH: 72f, PickerShowsText: true, PickerGap: 12f, PickerPad: 16f,
            TitleGlyph: true, SectionInline: true, SectionTop: 24f, SectionBottom: 8f),
        // Standard: the tuned design, unchanged (the 1000 column, the 208 x 172 cards).
        new(WidthTier.Standard, Target: 0f, StacksControls: false, TabHeight: 0f, TabsScroll: false, TabsBleed: false,
            Columns: 1, ColumnMaxW: 1000f, ColumnGap: 0f,
            ClassicThemePickerForm.Wrap, PickerCardW: 208f, PickerCardH: 172f, PickerThumbH: 92f, PickerShowsText: true, PickerGap: 12f, PickerPad: 16f,
            TitleGlyph: false, SectionInline: false, SectionTop: 32f, SectionBottom: 8f),
        // Huge (C-Settings-Huge, C-SettingsSections-Huge): two 1000 columns 32 apart, left-aligned; the picker's cards are the board's five-up 184.
        new(WidthTier.Huge, Target: 0f, StacksControls: false, TabHeight: 0f, TabsScroll: false, TabsBleed: false,
            Columns: 2, ColumnMaxW: 1000f, ColumnGap: 32f,
            ClassicThemePickerForm.Wrap, PickerCardW: 184f, PickerCardH: 172f, PickerThumbH: 92f, PickerShowsText: true, PickerGap: 12f, PickerPad: 16f,
            TitleGlyph: true, SectionInline: true, SectionTop: 28f, SectionBottom: 4f),
    ];

    /// <summary>The record of content tier <paramref name="content"/> (Huge 4K reads Huge's: Classic draws no 4K band).</summary>
    public static ClassicSettingsLayout For(WidthTier content) => s_records[Math.Min((int)content, (int)WidthTier.Huge)];

    /// <summary>The record for <paramref name="facts"/>; Standard's where no frame resolved the tiers (a view mounted alone).</summary>
    public static ClassicSettingsLayout Of(in TierFacts facts) => For(facts.Policy is null ? WidthTier.Standard : facts.Content);

    // ── the columns ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Where a tab's sections split over two columns: the number of leading groups that go in the left column, 1..n-1, chosen so the two columns' weights
    /// differ least (the left one wins a tie, so the first column is never the shorter by choice). -1 when the tab has fewer than two groups: it stays one column.</summary>
    /// <param name="weights">Each section's weight (its rows), in the tab's order.</param>
    public static int SplitAt(IReadOnlyList<int> weights)
    {
        int n = weights.Count;
        if (n < 2) return -1;
        int total = 0;
        for (int i = 0; i < n; i++) total += Math.Max(1, weights[i]);
        int best = 1, bestGap = int.MaxValue, left = 0;
        for (int k = 1; k < n; k++)
        {
            left += Math.Max(1, weights[k - 1]);
            int gap = Math.Abs(left - (total - left));
            if (gap < bestGap) { bestGap = gap; best = k; }
        }
        return best;
    }

    // ── the theme picker (arithmetic over the record, so a view never compares or computes with a measured width) ────────

    /// <summary>The columns of the Compact grid: two.</summary>
    public const int GridColumns = 2;

    /// <summary>A grid card's width in a window <paramref name="viewportWidth"/> wide: the window less the page gutters, the picker card's padding and borders, and the
    /// gaps between the columns, over the columns. Never under <see cref="GridMinCardW"/>.</summary>
    public static float GridCardWidth(this ClassicSettingsLayout layout, float viewportWidth, float gutter)
    {
        float room = viewportWidth - 2f * gutter - 2f * layout.PickerPad - 2f * PickerBorder - (GridColumns - 1) * layout.PickerGap;
        return MathF.Max(GridMinCardW, MathF.Floor(room / GridColumns));
    }

    /// <summary>The narrowest a grid card goes (a 300-wide window).</summary>
    public const float GridMinCardW = 96f;

    /// <summary>The picker card's border (the settings card's 1).</summary>
    public const float PickerBorder = 1f;

    /// <summary>The name row of a Compact grid card: at least this tall, so the card is a 40+ target (72 thumb + 36 name = 108).</summary>
    public const float GridNameRowH = 36f;
}
