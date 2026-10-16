// ── Themes/Classic/Structure/Rules/Tiers.Library.cs — the library page's layout ladder and its tier records (themes plan 3.1, 3.6; TC7) ───────────────
//
// THE LADDER (replaces the hand-rolled 640 collapse of `Entities\User.cs`, moved here by T10c1 and onto tier records by TC7). The page's two columns (the
// navigator and the pane or reader) are side by side from `SplitFrom` of the page's OWN width and fold into the one-column breadcrumb drill-in below it, held
// 24 DIP either way (the artist hero's `TierHysteresis`, the content ladder's own hold): a page that sits on the edge never flip-flops. It is the content ladder's
// Compact | Small edge (600), so the page gutter, the controls' size and the column fold change together; it stays a ladder of its own because the core host
// resolves it by id (`Wavee.Theming.LibraryLadders`) over the page's measured width and never sees a number. The host's `UseTier` resolves it in the page's first
// frame (a page mounted at Compact is one column from frame one) and commits a crossing on the first non-resized frame, where the two arms cross-fade.
//
// THE RECORDS (one per layout tier; boards C-Library / C-LibraryList with their -Compact / -Small / -Huge twins):
//
//   tier      columns                         head pad           rows of the head                          targets
//   Compact   ONE column, drill-in            16 / top 12        kind chips, title, sort button + view     40 (chips, sort, view cells, filter)
//                                                                 cells, filter; rows 12 apart
//   Small     split, the navigator at its     12 / top 16        title, word rail + view, filter           the stock 32 / 28
//             remembered width (grip 240-560)
//   Standard  split (the tuned design)        12 / top 16        as Small                                  the stock 32 / 28
//   Huge      split                           16 / top 24        as Small                                  the stock 32 / 28
//
// THE PERSISTED COLUMN WIDTHS (`library.<kind>.leftw` / `.midw`, clamped to the grips' range) are the host's: a tier never writes them. A tier that hides a column
// (the one-column arm) leaves the stored width alone, and a split tier that cannot afford it clips by flex (`RightMin` is the pane's floor, the navigator gives way down
// to the grip's 240), it does not rewrite it. The grid column counts are the fitted grid's own floor-fit over the navigator's width (116-164 cells): 2 columns at
// the 340 default, 2 at Compact, 4 at the Huge board's 560.
//
// THE NAVIGATOR'S RESTING WIDTH PER TIER (`Policy`, contract 1.21): while the user has never dragged the grip (the stored width is unset or the key's default), the
// navigator rests at the tier's width: 560 at Huge (C-Library-Huge: the 4-column grid), the artists' reader list 400 (C-LibraryList-Huge); 240 at Small for every kind
// (C-Library-Small / C-LibraryList-Small, contract 1.22); the key's own 340 / 280 at Standard. The host applies it (it never writes the store); the first drag makes
// the width the user's for good.
//
// THE GRID'S CELL FLOOR AT SMALL (`Policy.SmallCardCellMin`, contract 1.22): the full grid's size-S cell is 96 there (C-Library-Small: two 96 cells in the 240 navigator;
// the core's 116 would fit one). The S/M/L steps stay the core's; it moves the mounted grid's floor, never its mount key, so a crossing re-fits the grid in place.
//
// THE HUGE NAVIGATOR AND ALBUM PANE (C-Library-Huge): the navigator column is TINTED (`NavTinted`: the board's 2 % ink over the card) and the album pane's "Also by …
// in your library" leaves the bottom of the rows for a 300 ASIDE 32 right of them (`AlsoByAside`, `AlsoByAsideGap`): two columns of 140 covers, 16 / 20 apart. Below
// Huge the aside is width 0 (it stays in the pane's row, so a crossing eases it) and the strip is the rows' trailing band again.
//
// THE PANE HEADER PER TIER: the album and show panes' stated header is 156 tall with its 128 cover 18 from the words at Small and Standard (C-Library), and 200 with a
// 160 cover 24 from the words at Huge (C-Library-Huge).
//
// Public, so `Wavee.Tests` pins the ladder and the records (the assembly has no InternalsVisibleTo).

using System.Collections.Immutable;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>What Classic's library page is in one layout tier.</summary>
public sealed record ClassicLibraryLayout
{
    /// <summary>The layout tier the record is for (Compact is the one-column drill-in; Huge 4K reads Huge's).</summary>
    public required WidthTier Tier { get; init; }

    /// <summary>The columns are folded into the breadcrumb drill-in (the navigator, then the pane or reader, one at a time).</summary>
    public bool Single => Tier == WidthTier.Compact;

    /// <summary>The navigator head's padding (left, top, right, bottom).</summary>
    public required Edges4 HeadPad { get; init; }

    /// <summary>The gap between the head's rows (the kind chips, the title, the sort and view row, the filter).</summary>
    public required float HeadGap { get; init; }

    /// <summary>The height of the sort, view and filter controls: 40 at Compact, the stock 32 elsewhere.</summary>
    public required float ControlH { get; init; }

    /// <summary>The view toggle's cell edge: 40 at Compact (the board's radiogroup), 28 elsewhere.</summary>
    public required float ViewCell { get; init; }

    /// <summary>The kind chips (Albums, Artists, Podcasts, Audiobooks, Liked Songs) stand over the title and the sort is a button with its own menu: the Compact head, which
    /// has no nav pane beside it.</summary>
    public bool KindChips => Single;

    /// <summary>The kind chips' height (the Compact board's 40 pills).</summary>
    public required float ChipH { get; init; }

    /// <summary>The detail pane's floor beside the navigator: the navigator gives way (down to the grip's range floor) before the pane drops under it.</summary>
    public required float RightMin { get; init; }

    /// <summary>The pane's insets (the album and show panes' header, commands and "Also by" band, and the reader's bands): <see cref="ClassicTiers.PaneInset"/> at every tier.</summary>
    public float PaneInset => ClassicTiers.PaneInset;

    /// <summary>The album and show panes' cover edge (128; C-Library-Huge 160).</summary>
    public float PaneCover { get; init; } = ClassicLibraryPaneRules.PaneCover;

    /// <summary>The panes' STATED header height (the cover with its padding: 156; C-Library-Huge 200).</summary>
    public float PaneHeaderHeight { get; init; } = ClassicLibraryPaneRules.PaneHeaderHeight;

    /// <summary>The gap between the pane cover and its words (18; C-Library-Huge 24).</summary>
    public float PaneHeaderGap { get; init; } = ClassicLibraryPaneRules.HeaderGap;

    /// <summary>The navigator column carries its own faint tint over the card (C-Library-Huge, C-LibraryList-Huge); the tuned pages leave it on the card's own.</summary>
    public bool NavTinted { get; init; }

    /// <summary>The album pane's "Also by … in your library" aside beside the rows (C-Library-Huge: 300), 0 where the strip is the rows' trailing band.</summary>
    public float AlsoByAside { get; init; }

    /// <summary>The air between the rows and the aside (C-Library-Huge: 32).</summary>
    public float AlsoByAsideGap { get; init; }

    /// <summary>The full grid's size-S cell floor the navigator's shimmer lays its cards at: the host's mount floor for this tier (96 at Small, the core's 116 elsewhere).</summary>
    public float CardCellMin { get; init; } = LibraryNavMount.CardCellMinFull;

    /// <summary>The aside docks (<see cref="AlsoByAside"/> is not 0).</summary>
    public bool AlsoByDocked => AlsoByAside > 0f;
}

/// <summary>Classic's library ladder and records.</summary>
public static class ClassicLibraryTiers
{
    /// <summary>The split layout begins here (the content ladder's Small edge).</summary>
    public const float SplitFrom = 600f;

    /// <summary>The ladder's hold, both ways: entered below 576, left at 624.</summary>
    public const float SplitHold = 24f;

    /// <summary>The layout ladder: index 0 the one-column drill-in, 1 the split.</summary>
    public static TierLadder Layout { get; } = TierLadder.Of(new TierEdge(SplitFrom, NarrowHold: SplitHold, WidenHold: SplitHold));

    /// <summary>The ladders this family adds to <see cref="ClassicTiers.Policy"/>: the core host resolves them by id.</summary>
    public static ImmutableArray<NamedLadder> Ladders { get; } = ImmutableArray.Create(new NamedLadder(LibraryLadders.Layout, Layout));

    /// <summary>Compact's navigator head: 16 sides, 12 over it, the rows 12 apart, every control 40 (C-Library-Compact).</summary>
    static readonly ClassicLibraryLayout s_compact = new()
    {
        Tier = WidthTier.Compact, HeadPad = new Edges4(16f, 12f, 16f, 8f), HeadGap = 12f, ControlH = 40f, ViewCell = 40f, ChipH = 40f, RightMin = 0f,
    };

    /// <summary>Small's head is the tuned head (C-Library-Small draws 12 sides, 16 over it); its pane keeps 300 beside the navigator, whose grid starts at 96.</summary>
    static readonly ClassicLibraryLayout s_small = new()
    {
        Tier = WidthTier.Small, HeadPad = new Edges4(12f, 16f, 12f, 8f), HeadGap = 8f, ControlH = 32f, ViewCell = 28f, ChipH = 0f, RightMin = 300f,
        CardCellMin = SmallCardCellMin,
    };

    /// <summary>Standard: the tuned design.</summary>
    static readonly ClassicLibraryLayout s_standard = new()
    {
        Tier = WidthTier.Standard, HeadPad = new Edges4(12f, 16f, 12f, 8f), HeadGap = 8f, ControlH = 32f, ViewCell = 28f, ChipH = 0f, RightMin = 360f,
    };

    /// <summary>Huge (C-Library-Huge): 24 over the head, 16 sides, the tinted navigator and the album pane's 300 "Also by" aside.</summary>
    static readonly ClassicLibraryLayout s_huge = new()
    {
        Tier = WidthTier.Huge, HeadPad = new Edges4(16f, 24f, 16f, 8f), HeadGap = 8f, ControlH = 32f, ViewCell = 28f, ChipH = 0f, RightMin = 480f,
        PaneCover = HugePaneCover, PaneHeaderHeight = HugePaneHeader, PaneHeaderGap = HugePaneGap,
        NavTinted = true, AlsoByAside = HugeAlsoByAside, AlsoByAsideGap = HugeAlsoByGap,
    };

    /// <summary>The Huge album pane's "Also by" aside (C-Library-Huge): 300 wide, 32 right of the rows.</summary>
    public const float HugeAlsoByAside = 300f, HugeAlsoByGap = 32f;

    /// <summary>The navigator's resting width at Small while the user never dragged it, every kind (C-Library-Small, C-LibraryList-Small: 240).</summary>
    public const float SmallNavWidth = 240f;

    /// <summary>The full grid's size-S cell floor at Small (C-Library-Small: two 96 cells in the 240 navigator).</summary>
    public const float SmallCardCellMin = 96f;

    /// <summary>The Huge pane header (C-Library-Huge): a 160 cover in a 200 band, 24 from the words.</summary>
    public const float HugePaneCover = 160f, HugePaneHeader = 200f, HugePaneGap = 24f;

    /// <summary>The navigator's resting width at Huge while the user never dragged it (C-Library-Huge 560: the 4-column grid), and the artists' reader list's
    /// (C-LibraryList-Huge 400).</summary>
    public const float HugeNavWidth = 560f, HugeReaderNavWidth = 400f;

    /// <summary>The policy the structure publishes for the core host (`ThemePolicies.LibraryTiers`).</summary>
    public static LibraryTierPolicy Policy { get; } = new()
    {
        HugeNavWidth = HugeNavWidth, HugeReaderNavWidth = HugeReaderNavWidth,
        SmallNavWidth = SmallNavWidth, SmallReaderNavWidth = SmallNavWidth, SmallCardCellMin = SmallCardCellMin,
    };

    /// <summary>The record for <paramref name="facts"/>' content tier while the page is <paramref name="single"/> (the layout ladder's answer: the host's tier signal) or split. A
    /// split page in a Compact content tier (the ladders' holds differ by a frame) reads Small's; Standard's where no frame resolved the tiers (a view mounted alone).</summary>
    public static ClassicLibraryLayout Of(in TierFacts facts, bool single)
        => For(facts.Policy is null ? WidthTier.Standard : facts.Content, single);

    /// <summary>The record for content tier <paramref name="content"/> (Huge 4K reads Huge's).</summary>
    public static ClassicLibraryLayout For(WidthTier content, bool single)
    {
        if (single) return s_compact;
        return (WidthTier)Math.Min((int)content, (int)WidthTier.Huge) switch
        {
            WidthTier.Compact or WidthTier.Small => s_small,
            WidthTier.Standard => s_standard,
            _ => s_huge,
        };
    }

    /// <summary>The artist reader's STACKED verb row (Y3) folds shuffle and ↗ into one ⋯ menu when its measured lane is narrower than the whole
    /// row (<see cref="Artist.ReaderShape.ActionsW"/>, 316), so the row never clips (the Small board's "More: Shuffle, Open artist page").
    /// An unmeasured lane (≤ 0) folds: the first frame never overflows.</summary>
    public static bool FoldVerbs(float laneWidth) => laneWidth < Artist.ReaderShape.ActionsW;
}
