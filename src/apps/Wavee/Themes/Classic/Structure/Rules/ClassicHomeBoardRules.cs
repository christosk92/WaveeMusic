// ── Themes/Classic/Structure/Rules/ClassicHomeBoardRules.cs — the numbers of Classic's pinned Home board (pinned-home-tech.md 1.7; spec 4.2, 8.3) ──
//
// Classic's speed dial and shelves (spec 4.2): a speed-dial cell is 1 column x 1 unit (64: the cover flush left, the name, one line), a card 1 column x
// 3 units (216), a wide card 2 columns x 3 units, a shelf the whole width at its chapter's own height. Columns on Classic's content tiers: Compact 1,
// Small 2, Standard 3 to 4, Huge 5 to 6 (a floor-fit inside the tier). Gap 12, a group title in the chapter header's type, 24 between groups.
//
// EDIT HOME gives every block of two units or more a 40 DIP strip of its own above it for its chips (`PackMetrics.ToolStrip`), so no chip sits on a
// tile's content; a speed-dial cell keeps its chips at its right, where its text is cut short. The rows grow by the strip through the grid's re-flow.
//
// ONE record per CONTENT tier, read through `ClassicHomeBoardTiers.Of(ctx.Tier())`; a view never compares a width with a number (WVTH013).
//
// A SHELF's height is its chapter's: the chapter reserves its height from the Home tier record at the fitted width, and the board takes the record's
// estimate first (`FullEstimate`), then the chapter's measured height: the box follows the body by the 250 ms re-flow, never by a snap.

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Foundation;
using FluentGpu.Scene;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>What Classic's pinned Home board is in one content tier.</summary>
public sealed record ClassicHomeBoardLayout
{
    /// <summary>The content tier the record is for.</summary>
    public required WidthTier Tier { get; init; }

    /// <summary>The columns: a floor-fit inside the tier.</summary>
    public required ClassicGridBand Columns { get; init; }

    /// <summary>One row unit (64: a speed-dial cell).</summary>
    public required float Unit { get; init; }

    /// <summary>The gap between tiles, both ways (12).</summary>
    public required float Gap { get; init; }

    /// <summary>The air above every group title but the first (24).</summary>
    public required float GroupGap { get; init; }

    /// <summary>A group title's row: the chapter header's title and the air under it (44).</summary>
    public required float GroupHead { get; init; }

    /// <summary>The empty Pinned group's dashed drop row (one unit; two at Compact, where its sentence wraps).</summary>
    public required float EmptyRow { get; init; }

    /// <summary>A group's "Add to {group}" row in Edit Home, closed (a 40 button row) and open (its strip of add cards).</summary>
    public required float AddRow { get; init; }

    /// <summary>The open add row's height: its head and one row of add cards.</summary>
    public required float AddRowOpen { get; init; }

    /// <summary>The "Customize Home" link's row at the board's end.</summary>
    public required float EndRow { get; init; }

    /// <summary>A speed-dial cell's span (columns, units).</summary>
    public required (byte Cols, byte Rows) S { get; init; }

    /// <summary>A card's span.</summary>
    public required (byte Cols, byte Rows) M { get; init; }

    /// <summary>A wide card's span.</summary>
    public required (byte Cols, byte Rows) L { get; init; }

    /// <summary>The narrowest an open tile is (columns): its radio rows want two columns where there are two.</summary>
    public required byte OpenMinCols { get; init; }

    /// <summary>The chip strip above a block of two units or more in Edit Home.</summary>
    public required float ChipStrip { get; init; }

    /// <summary>A chip's box (32 drawn; 40 at Compact, the target floor).</summary>
    public required float ChipBox { get; init; }

    /// <summary>The board's width from which the edit bar stands in the facet row's end with its caption and find field; narrower, the bar keeps its
    /// buttons there and the find field takes its own line under the band (spec 8.3 "the bar wraps and the field takes the width"): the facet words
    /// and the whole bar need about this much.</summary>
    public required float InlineFrom { get; init; }

    /// <summary>Whether the edit bar holds its caption and find field at a board width.</summary>
    public bool BarInline(float width) => width >= InlineFrom;

    /// <summary>The find field's width in the bar (inline), 0 when it takes its own line.</summary>
    public required float FindW { get; init; }

    /// <summary>An add card's width in an add row or the find strip.</summary>
    public required float AddCardW { get; init; }

    /// <summary>The page's columns at a content width.</summary>
    public int ColumnsAt(float width) => Columns.Columns(width, Gap);

    /// <summary>The pack metrics at a content width; Edit Home adds the chip strip.</summary>
    public PackMetrics Metrics(float width, bool editing) => new(ColumnsAt(width), Unit, Gap, GroupGap, editing ? ChipStrip : 0f, M.Rows - 1);
}

/// <summary>Classic's board records.</summary>
public static class ClassicHomeBoardTiers
{
    /// <summary>The record for <paramref name="facts"/> (Standard's where no frame resolved the tiers).</summary>
    public static ClassicHomeBoardLayout Of(in TierFacts facts) => For(facts.Policy is null ? WidthTier.Standard : facts.Content);

    /// <summary>The record for content tier <paramref name="content"/> (4K reads Huge's: Classic draws no 4K band).</summary>
    public static ClassicHomeBoardLayout For(WidthTier content) => (WidthTier)Math.Min((int)content, (int)WidthTier.Huge) switch
    {
        WidthTier.Compact => Base with
        {
            Tier = WidthTier.Compact, Columns = ClassicGridBand.Fixed(1), EmptyRow = 2f * UnitH + GapW, ChipBox = 40f,
            InlineFrom = float.PositiveInfinity, AddCardW = 220f,
        },
        WidthTier.Small => Base with { Tier = WidthTier.Small, Columns = new ClassicGridBand(260f, 2), InlineFrom = float.PositiveInfinity },
        WidthTier.Standard => Base with { Tier = WidthTier.Standard, Columns = new ClassicGridBand(240f, 4) },
        _ => Base with { Tier = WidthTier.Huge, Columns = new ClassicGridBand(280f, 6), FindW = 240f },
    };

    const float UnitH = 64f, GapW = 12f;

    static readonly ClassicHomeBoardLayout Base = new()
    {
        Tier = WidthTier.Standard, Columns = new ClassicGridBand(240f, 4),
        Unit = UnitH, Gap = GapW, GroupGap = 24f, GroupHead = 44f, EmptyRow = UnitH, AddRow = 40f, AddRowOpen = 176f, EndRow = 40f,
        S = (1, 1), M = (1, 3), L = (2, 3), OpenMinCols = 2, ChipStrip = 40f, ChipBox = 32f,
        InlineFrom = 1080f, FindW = 200f, AddCardW = 240f,
    };
}

/// <summary>The constants of Classic's board views.</summary>
public static class ClassicHomeBoardRules
{
    /// <summary>A tile's inner padding (12; 16 on a card).</summary>
    public const float TilePad = 12f, CardPad = 16f;

    /// <summary>A card's cover (96) and a wide card's (the card's height less its padding: 184).</summary>
    public const float CardArt = 96f, WideArt = 184f;

    /// <summary>A speed-dial cell's name lane gives up this much at its end in Edit Home: its two chips stand there.</summary>
    public const float CellChipsW = 84f;

    /// <summary>The list rows a card (2) and a wide card (3) show under their head.</summary>
    public const int CardRows = 2, WideRows = 3;

    /// <summary>Up next and Recents list rows inside a card: a 40 row with a 32 cover.</summary>
    public const float ListRowH = 40f, ListArt = 32f;

    /// <summary>The widget head inside a card: a 16 glyph, the title, a line (32 tall).</summary>
    public const float WidgetHeadH = 32f, WidgetGlyph = 16f;

    /// <summary>A dashed outline's runs (the edit outline, the drop row, the slot).</summary>
    public const float DashOn = 4f, DashOff = 4f;

    /// <summary>The ring around a tile just added and the open tile's outline: 2 DIP; the ring shows for 1.8 s.</summary>
    public const float RingW = 2f;
    public const int RingMs = 1800;

    /// <summary>The now-playing card's progress hairline.</summary>
    public const float ProgressH = 3f;

    /// <summary>An open tile's options: its head (the name, Done) and one row per radio value, a label row per option.</summary>
    public const float OptionsHeadH = 48f, OptionLabelH = 28f, OptionRadioRowH = 36f, OptionToggleH = 44f;

    /// <summary>A shelf's estimate where nothing better is known (a header and one row of covers).</summary>
    public const float FullFallback = 300f;

    /// <summary>The release list's rows a full tile reserves before it has measured.</summary>
    public const int ReleaseRowsEstimate = 3;

    /// <summary>An add card in a strip: its cover or glyph (48), the name, one line, "Add".</summary>
    public const float AddArt = 48f;

    /// <summary>The add row's open strip: its head line and the strip's padding.</summary>
    public const float AddHeadH = 40f;

    /// <summary>The find strip under the bar: its count line, the cards' row height and its padding.</summary>
    public const float FindCountH = 24f, FindRowH = 112f;

    /// <summary>"/" (VK_OEM_2): the find field.</summary>
    public const int SlashKey = 191;

    /// <summary>Delete (VK_DELETE).</summary>
    public const int DeleteKey = 46;

    /// <summary>Half (a round cover's corner is half its edge).</summary>
    public const float Half = 0.5f;

    /// <summary>Reduced motion's fade.</summary>
    public const float ReducedMs = 120f;

    /// <summary>A strip opening and folding (the find strip, an add row): its height eases (the engine's flow reveal), what is under it rides it.</summary>
    public static LayoutTransition Reveal => Design.Reduced
        ? new(TransitionChannels.Size, TransitionDynamics.Tween(ReducedMs, Easing.SmoothOut), Size: SizeMode.FlowReveal)
        : new(TransitionChannels.Size, TransitionDynamics.Tween(ClassicTierMotion.ReflowMs, Easing.SmoothOut), Size: SizeMode.FlowReveal);

    /// <summary>What arrives in a box that has already grown (an add row's cards, the find strip's results): a short fade, no travel.</summary>
    public static MotionTokenDef FadeIn => MotionTokenDef.Eased(Design.Motion.Faster, Easing.FluentStandard, ReducedMotionPolicy.KeepFade);

    /// <summary>A speed-dial cell's chip lane opening and folding at its right: its width eases, the name lane gives way.</summary>
    public static LayoutTransition ChipLane => new(TransitionChannels.Size,
        TransitionDynamics.Tween(Design.Reduced ? ReducedMs : ClassicTierMotion.ReflowMs, Easing.SmoothOut), Size: SizeMode.Reveal);

    /// <summary>The theme's auto size of a source (spec 4.3: a pin is a speed-dial cell; a widget a card; Recents wide; sections a shelf).</summary>
    public static HomeSize AutoOf(HomeSourceDef? source) => source?.CanonicalAuto is { } a && a != HomeSize.Auto ? a : HomeSize.S;

    /// <summary>The size an entry packs at: its own, else the theme's auto.</summary>
    public static HomeSize SizeOf(BoardEntry e) => e.Size != HomeSize.Auto ? e.Size : AutoOf(e.Source);

    /// <summary>The form a packed box can carry (spec 4.2): the class is the floor; a card the fill made two columns wide draws the wide form, a cell that
    /// got three units draws the card. A shelf is never decided here: it always draws its full form.</summary>
    public static HomeTileForm FormOf(in PackBox box, ClassicHomeBoardLayout lay)
    {
        if (box.Rows >= lay.M.Rows - 0.01f) return box.Cols >= lay.L.Cols ? HomeTileForm.L : HomeTileForm.M;
        return HomeTileForm.S;
    }

    /// <summary>The pack entry of a board entry. <paramref name="fullExtent"/> is a shelf's height (measured, else estimated); <paramref name="openExtent"/>
    /// the open tile's (0: not open).</summary>
    public static PackEntry EntryOf(BoardEntry e, ClassicHomeBoardLayout lay, float fullExtent, float openExtent)
    {
        if (e.Kind == BoardEntryKind.GroupHead) return new PackEntry(PackEntryKind.GroupHead, Extent: lay.GroupHead);
        if (e.Hidden || IsHidden(e)) return new PackEntry(PackEntryKind.Tile, Flags: PackFlags.Hidden);
        var size = SizeOf(e);
        var flags = e.Size == HomeSize.Auto ? PackFlags.Auto : PackFlags.None;
        if (size == HomeSize.Full)
            return new PackEntry(PackEntryKind.Band, Flags: flags, Extent: MathF.Max(fullExtent, openExtent));
        var (c, r) = size switch { HomeSize.L => lay.L, HomeSize.M => lay.M, _ => lay.S };
        if (openExtent > 0f) flags |= PackFlags.Open;
        return new PackEntry(PackEntryKind.Tile, c, r, flags, openExtent, lay.OpenMinCols);
    }

    /// <summary>A placed section with no match in the answer keeps its place and takes no room.</summary>
    public static bool IsHidden(BoardEntry e) => e.Kind == BoardEntryKind.Placeholder && e.Template == SectionTemplate.None;

    /// <summary>The open tile's height: its head, then per option a label and one radio row per value (a toggle one row), then live updates.</summary>
    public static float OpenExtent(HomeSourceDef? source, ClassicHomeBoardLayout lay)
    {
        if (source is null) return 0f;
        float h = OptionsHeadH + CardPad;
        foreach (var o in source.Options)
            h += o.Kind == OptionKind.Toggle ? OptionToggleH : OptionLabelH + OptionRadioRowH * o.Values.Length;
        if (source.Live) h += OptionToggleH;
        return MathF.Max(h, lay.M.Rows * lay.Unit + (lay.M.Rows - 1) * lay.Gap);
    }
}
