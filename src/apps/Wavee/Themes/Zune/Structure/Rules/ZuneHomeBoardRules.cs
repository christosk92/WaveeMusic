// ── Themes/Zune/Structure/Rules/ZuneHomeBoardRules.cs — the numbers of Zune's pinned Home board (pinned-home-tech.md 1.7; spec 4.2, 8.2) ──────
//
// Windows Phone Start, kept on Home: SQUARE cells (the pack's unit is the column width), 8 apart. A small tile is 1 x 1, a medium 2 x 2, a wide 4 x 2,
// a band (`full`) the whole row at its body's own height (a chapter of today's Home: the daylist strip, a shelf, the release list, the history).
// Columns by the content tier: Compact 4, Small 6 to 8, Standard 8 to 12, Huge 12, Huge 4K 16 (inside a tier the most columns whose cell is still at
// least `MinCell`). Group titles are display type (40/48 light, lowercase), ONE line, bleeding under a fade at the board's edge, never smaller.
//
// THE PAGE TITLE ("good evening, …", the hero rung 64/72 light, lowercase) heads the board and stays put while all items slides in under it; it WRAPS
// at Compact (owner rule: Zune display type never shrinks). Undo is a lowercase word in a title's own line (spec 8.2): the title of the group the last
// change was in, else the page title.
//
// CUSTOMIZE: the selected tile comes forward, every other tile falls back to 94 % and dims; the unpin badge hangs at the top right, the resize badge at
// the bottom right, 36 drawn in a 40 target (the Compact floor), inside the tile (the pack's wrapper clips at its box). A press held 600 ms (mouse)
// or 500 ms (touch), a right-click or Enter on a tile enters it. The app bar at Home's foot: 48 rings in a 72 row; its ⋯ slides it up to show the
// labels and the menu words.
//
// ONE record per CONTENT tier, read through `ZuneHomeBoardTiers.Of(ctx.Tier())`; a view never compares a width with a number (WVTH013). Literal
// numbers are allowed here (WVTH003 exempts `Rules\**`).

using System.Collections.Immutable;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Foundation;
using FluentGpu.Scene;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Zune;

/// <summary>What Zune's pinned Home board is in one content tier.</summary>
public sealed record ZuneHomeBoardLayout
{
    /// <summary>The content tier the record is for.</summary>
    public required WidthTier Tier { get; init; }

    /// <summary>The column counts the tier may use, fewest first: the board takes the most whose cell is still <see cref="MinCell"/> wide.</summary>
    public required ImmutableArray<int> ColumnSteps { get; init; }

    /// <summary>The narrowest cell a column count may give.</summary>
    public float MinCell { get; init; } = 72f;

    /// <summary>The gap between tiles, both ways (8: Start's).</summary>
    public float Gap { get; init; } = 8f;

    /// <summary>The air above every group head but the first.</summary>
    public float GroupGap { get; init; } = 24f;

    /// <summary>A group head's row: the 40/48 light title and the air under it.</summary>
    public float GroupHead { get; init; } = 60f;

    /// <summary>The empty Pinned group's row: "nothing pinned" (26/32 light) over its hint (13/18).</summary>
    public float EmptyRow { get; init; } = 72f;

    /// <summary>A small tile's span (columns, rows of cells).</summary>
    public (byte Cols, byte Rows) S { get; init; } = (1, 1);

    /// <summary>A medium tile's span.</summary>
    public (byte Cols, byte Rows) M { get; init; } = (2, 2);

    /// <summary>A wide tile's span.</summary>
    public (byte Cols, byte Rows) L { get; init; } = (4, 2);

    /// <summary>The narrowest a turned-over tile is (columns): its back carries the choices as words.</summary>
    public byte OpenMinCols { get; init; } = 4;

    /// <summary>The air between the page title and the board, and under the board before the "all items" ring.</summary>
    public float TitleGap { get; init; } = 8f;

    /// <summary>The page's foot under the board: the app bar's height (the bar lies over the page's foot) and its air.</summary>
    public float FootReserve { get; init; } = ZuneHomeBoardRules.BarH + 24f;

    /// <summary>The "all items" ring's row at the end of the board.</summary>
    public float EndRowTop { get; init; } = 28f;

    /// <summary>An all-items row: its square (62) and its least height (70).</summary>
    public float ItemArt { get; init; } = 62f;

    /// <summary>An all-items row's least height.</summary>
    public float ItemRowH { get; init; } = 70f;

    /// <summary>The all-items list's widest (the list reads as a column, as the phone's App list).</summary>
    public float ItemsMaxW { get; init; } = 780f;

    /// <summary>The pivots' gap (26) and the all-items ring (44).</summary>
    public float PivotGap { get; init; } = 26f;

    /// <summary>The tier's columns at a board width.</summary>
    public int ColumnsAt(float width)
    {
        var steps = ColumnSteps;
        int best = steps[0];
        for (int i = 0; i < steps.Length; i++)
        {
            int c = steps[i];
            float cell = (width - (c - 1) * Gap) / c;
            if (cell >= MinCell) best = c;
        }
        return best;
    }

    /// <summary>A cell's edge at a board width.</summary>
    public float CellAt(float width)
    {
        int c = ColumnsAt(width);
        return c <= 0 ? 0f : MathF.Max(0f, (width - (c - 1) * Gap) / c);
    }

    /// <summary>The pack metrics at a board width: square cells (unit 0: the column width).</summary>
    public PackMetrics Metrics(float width) => new(ColumnsAt(width), 0f, Gap, GroupGap);
}

/// <summary>Zune's board records.</summary>
public static class ZuneHomeBoardTiers
{
    /// <summary>The record for <paramref name="facts"/> (Standard's where no frame resolved the tiers).</summary>
    public static ZuneHomeBoardLayout Of(in TierFacts facts) => For(facts.Policy is null ? WidthTier.Standard : facts.Content);

    /// <summary>The record for content tier <paramref name="content"/>.</summary>
    public static ZuneHomeBoardLayout For(WidthTier content) => s_records[Math.Clamp((int)content, 0, s_records.Length - 1)];

    private static readonly ZuneHomeBoardLayout[] s_records =
    [
        new() { Tier = WidthTier.Compact, ColumnSteps = [4], GroupGap = 20f, EmptyRow = 88f, PivotGap = 22f, ItemArt = 56f, ItemRowH = 64f },
        new() { Tier = WidthTier.Small, ColumnSteps = [6, 8] },
        new() { Tier = WidthTier.Standard, ColumnSteps = [8, 12] },
        new() { Tier = WidthTier.Huge, ColumnSteps = [12], GroupGap = 32f },
        new() { Tier = WidthTier.Huge4K, ColumnSteps = [16], GroupGap = 32f },
    ];
}

/// <summary>The constants of Zune's board views.</summary>
public static class ZuneHomeBoardRules
{
    /// <summary>The board's own node keys (the contract's are in <see cref="HomeBoardKeys"/>).</summary>
    public const string TitleKey = "home:title", TitleTextKey = "home:title:text", EndKey = "home:allitems", BarMoreKey = "home:edit:more",
        BarMenuKey = "home:edit:menu", PivotPrefix = "home:add:pivot:", SearchRingKey = "home:add:search", FieldKey = "home:add:field",
        BackRingKey = "home:add:back", RowPrefix = "home:add:row:", RowPinPrefix = "home:add:pin:", EmptyKey = "home:empty:pinned",
        UnpinSuffix = ":unpin", ResizeSuffix = ":resize", BackKey = "home:tile-back", EditWordKey = "home:edit:word";

    /// <summary>Press and hold: 600 ms with a mouse, 500 ms with a finger (spec 8.2).</summary>
    public const float HoldMouseMs = 600f, HoldTouchMs = 500f;

    /// <summary>Customize: every tile but the selected one falls back to 94 % and dims to 55 %.</summary>
    public const float Recede = 0.94f, RecedeDim = 0.55f;

    /// <summary>The recede and the return (Fluent's 250 ms re-flow clock); reduced motion: no travel, a 120 ms fade.</summary>
    public const float RecedeMs = 250f, ReducedMs = 120f;

    /// <summary>A badge: a 36 disc in a 40 target, 2 in from the tile's corner; its glyph 16.</summary>
    public const float BadgeEdge = 36f, BadgeHit = 40f, BadgeInset = 2f, BadgeGlyph = 16f, BadgeStroke = 2f;

    /// <summary>The lane a medium or wide tile's text leaves at its end for the badges (edit tools never sit on text).</summary>
    public const float BadgeLane = 44f;

    /// <summary>The selected tile's outline (2, the ink).</summary>
    public const float SelectedRing = 2f;

    /// <summary>The ring around a tile just added: 2 DIP of the accent, shown for 1.8 s.</summary>
    public const float RingW = 2f;

    /// <summary>A tile's inner padding (10) and a small tile's (8).</summary>
    public const float TilePad = 10f, SmallPad = 8f;

    /// <summary>The scrim under a medium tile's name: its height (the 28 lead and the name), its alpha at the foot.</summary>
    public const float ScrimH = 64f;
    public const byte ScrimAlpha = 230;

    /// <summary>The medium form's name scrim: black from 0 to 90 % at the foot (white names read on a pale cover too).</summary>
    public static readonly GradientSpec NameScrim = FluentGpu.Dsl.Ui.GradientDown(
        new GradientStop(0f, ColorF.FromRgba(0, 0, 0, 0)),
        new GradientStop(1f, ColorF.FromRgba(0, 0, 0, ScrimAlpha)));

    /// <summary>The air between a name and its line (2), and between a ring and its label (4).</summary>
    public const float TextGap = 2f, LabelGap = 4f;

    /// <summary>A widget's glyph on a small tile (24) and in a tile's head (16).</summary>
    public const float WidgetGlyph = 24f, HeadGlyph = 16f;

    /// <summary>List rows inside a tile: 44 rows of 36 art, 4 apart.</summary>
    public const float ListRowH = 44f, ListArt = 36f, ListGap = 4f;

    /// <summary>A widget lists two rows in a medium tile, four (two columns of two, 12 apart) in a wide one.</summary>
    public const int ListRowsM = 2, ListRowsL = 4;
    public const float ListColGap = 12f;

    /// <summary>The now-playing tile's progress line (3).</summary>
    public const float ProgressH = 3f;

    /// <summary>A tile's art decode (square px, cover-fit into the tile) and a live preview's square where no cell gives the height.</summary>
    public const float ArtDecodePx = 400f, PreviewSquare = 120f;

    /// <summary>A tile's play ring (36).</summary>
    public const float TileRing = 36f;

    /// <summary>A full band's height before its body has laid out once, where nothing better is known (a heading row and one shelf of covers).</summary>
    public const float FullFallback = 300f;

    /// <summary>The tile's back: its padding, its head (the name in 26/30 light and "done"), one option (its 13 label over a line of words), the words'
    /// gap, and the gap between options.</summary>
    public const float BackPadX = 14f, BackPadTop = 10f, BackPadBottom = 14f, BackHeadH = 40f, BackLabelH = 18f, BackWordH = 40f, BackWordGap = 18f,
        BackOptionGap = 12f;

    /// <summary>How many words fit on a line of the back at its least width (4 cells): the open extent reserves one line per this many values.</summary>
    public const int BackWordsPerLine = 3;

    /// <summary>The undo word: shown for 5 s after a change, then at half strength; its fade 200 ms; the slop of its target.</summary>
    public const float UndoFreshMs = 5000f, UndoRest = 0.5f, UndoFadeMs = 200f, UndoPadX = 4f, UndoPadY = 6f, UndoGap = 16f;

    /// <summary>A title's fade at the board's edge (a group title that does not fit bleeds under it).</summary>
    public const float TitleFade = 32f;

    /// <summary>The app bar: its row (72: a 48 ring and the air), the ring (48, 2 stroke, 22 glyph), the buttons 22 apart and 60 wide, the labels
    /// (11/14) that show when the ⋯ slides it up, the ⋯'s target, the menu words (24/30 light, 9 / 24 padding).</summary>
    public const float BarH = 72f, BarRing = 48f, BarRingStroke = 2f, BarGlyph = 20f, BarButtonW = 60f, BarGap = 22f, BarPadTop = 12f,
        BarLabelH = 22f, BarMoreW = 52f, BarMoreH = 40f, BarMenuPadX = 24f, BarMenuPadY = 9f, BarMenuFoot = 14f;

    /// <summary>The all-items section: the pivot row's air, the ring (44), the field (48 tall, 2 rule, at most 780), the rows' gap, the hint's air.</summary>
    public const float PivotRowTop = 14f, PivotRowBottom = 12f, ItemsRing = 44f, FieldH = 48f, FieldGap = 14f, RowGap = 6f, ItemGap = 14f, HintTop = 22f;

    /// <summary>A row's square glyph (28) on its accent plate.</summary>
    public const float ItemGlyph = 28f;

    /// <summary>The search field's focus request: retried every 16 ms, at most 10 times, until its editor is realized.</summary>
    public const float FocusRetryMs = 16f;
    public const int FocusRetries = 10;

    /// <summary>The search's count is spoken once the typing pauses this long.</summary>
    public const float SayAfterMs = 400f;

    /// <summary>"/" (VK_OEM_2): all items' search.</summary>
    public const int SlashKey = 191;

    /// <summary>The all-items section sliding in under the title (and the board sliding away): a short travel and a fade.</summary>
    public const float SectionSlide = 48f;

    /// <summary>The menu and labels opening (the bar's height eases).</summary>
    public static LayoutTransition BarReveal => new(TransitionChannels.Size, TransitionDynamics.Tween(RecedeMs, Easing.SmoothOut), Size: SizeMode.FlowReveal);

    /// <summary>The theme's auto size of a source (spec 4.3): a pin is medium in Zune (a small tile hides the name); a fill cell small.</summary>
    public static HomeSize AutoOf(BoardEntry e)
    {
        if (e.Raw is PinSource && e.Kind != BoardEntryKind.FillCell) return HomeSize.M;
        return e.Source?.CanonicalAuto is { } a && a != HomeSize.Auto ? a : HomeSize.S;
    }

    /// <summary>The size an entry packs at: its own, else the theme's auto.</summary>
    public static HomeSize SizeOf(BoardEntry e) => e.Size != HomeSize.Auto ? e.Size : AutoOf(e);

    /// <summary>The form a packed box can carry (the concept's: 4 columns and 2 rows draw the wide form, 2 and 2 the medium, else the small). A band is never
    /// decided here: it always draws its full form.</summary>
    public static HomeTileForm FormOf(in PackBox box, ZuneHomeBoardLayout lay)
    {
        if (box.Rows >= lay.M.Rows - 0.1f)
        {
            if (box.Cols >= lay.L.Cols) return HomeTileForm.L;
            if (box.Cols >= lay.M.Cols) return HomeTileForm.M;
        }
        return HomeTileForm.S;
    }

    /// <summary>A hidden entry (a placed section the answer lacks, a hidden feed section) keeps its place and takes no room.</summary>
    public static bool IsHidden(BoardEntry e) => e.Hidden;

    /// <summary>The pack entry of a board entry. <paramref name="fullExtent"/> is a band's height (measured, else estimated); <paramref name="openExtent"/>
    /// the turned-over tile's (0: not open).</summary>
    public static PackEntry EntryOf(BoardEntry e, ZuneHomeBoardLayout lay, float fullExtent, float openExtent)
    {
        if (e.Kind == BoardEntryKind.GroupHead) return new PackEntry(PackEntryKind.GroupHead, Extent: lay.GroupHead);
        if (IsHidden(e)) return new PackEntry(PackEntryKind.Tile, Flags: PackFlags.Hidden);
        var size = SizeOf(e);
        var flags = e.Size == HomeSize.Auto ? PackFlags.Auto : PackFlags.None;
        if (size == HomeSize.Full) return new PackEntry(PackEntryKind.Band, Flags: flags, Extent: MathF.Max(fullExtent, openExtent));
        var (c, r) = size switch { HomeSize.L => lay.L, HomeSize.M => lay.M, _ => lay.S };
        if (openExtent > 0f) flags |= PackFlags.Open;
        return new PackEntry(PackEntryKind.Tile, c, r, flags, openExtent, lay.OpenMinCols);
    }

    /// <summary>The turned-over tile's least height: the head, then per option its label over its lines of words (one line per
    /// <see cref="BackWordsPerLine"/> values), the live choice one more option; the back scrolls when the words need more.</summary>
    public static float OpenExtent(HomeSourceDef? source)
    {
        if (source is null) return 0f;
        float h = BackPadTop + BackHeadH + BackPadBottom;
        int n = 0;
        foreach (var o in source.Options)
        {
            int lines = Math.Max(1, (o.Values.Length + BackWordsPerLine - 1) / BackWordsPerLine);
            h += BackLabelH + lines * BackWordH;
            n++;
        }
        if (n > 0) h += n * BackOptionGap;
        return h;
    }

    /// <summary>The ⋯ menu's height (its words) and the labels' row: what the bar grows by when it opens.</summary>
    public static float MenuH(int words) => words * (BarMenuPadY * 2f + 30f) + BarMenuFoot;
}
