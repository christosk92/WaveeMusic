// ── Themes/WinUi/Structure/Rules/WinUiHomeBoardRules.cs — the numbers of WinUI's pinned Home board (pinned-home-tech.md 1.7) ──────────
//
// Windows 11 Start and the Widgets board (spec 4.2): a row tile is 1 column x 1 unit (72: a 48 cover, the name, one line), a card 1 column x 3 units
// (240), a double card 2 columns x 3 units, a full row the whole width at its body's own height. Columns by the content tier: Compact 1, Small 2,
// Standard 3 to 4, Huge 5 to 6 (a floor-fit inside the tier, `WinUiGridBand`). Gap 12, a quiet 14 semibold group label, 20 between groups.
//
// ONE record per CONTENT tier, read through `WinUiHomeBoardTiers.Of(ctx.Tier())`; a view never compares a width with a number (WVTH013).
//
// A FULL ROW's height is its body's: a chapter (a shelf, the release list, the Recents grid, the daylist card) reserves its height from the Home
// tier record at the fitted width, and the board takes that same number (`FullExtent`, the estimate a full row packs at before its body has laid
// out once), then the body's measured height (`WinUiHomeBoard`): the box follows the body by the 250 ms re-flow, never by a snap.

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Foundation;
using FluentGpu.Scene;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.WinUi;

/// <summary>What WinUI's pinned Home board is in one content tier.</summary>
public sealed record WinUiHomeBoardLayout
{
    /// <summary>The content tier the record is for.</summary>
    public required WidthTier Tier { get; init; }

    /// <summary>The columns: a floor-fit inside the tier.</summary>
    public required WinUiGridBand Columns { get; init; }

    /// <summary>One row unit (72: a row tile).</summary>
    public required float Unit { get; init; }

    /// <summary>The gap between tiles, both ways (12).</summary>
    public required float Gap { get; init; }

    /// <summary>The air above every group head but the first (20).</summary>
    public required float GroupGap { get; init; }

    /// <summary>A group head's row: the 14 semibold label and the air under it (34).</summary>
    public required float GroupHead { get; init; }

    /// <summary>The Pinned group's head, the concept's "Your shortcuts": the chapter title over its subtitle beside the Shortcuts / Recently played switch and
    /// the "Listening history" link, and the air under it (Compact: the title row, then the switch on its own line).</summary>
    public required float ShortcutsHead { get; init; }

    /// <summary>The shortcuts grid's Shortcuts / Recently played switch's height: the header's tools row (32), Compact's target floor (40).</summary>
    public required float FaceSwitchH { get; init; }

    /// <summary>The empty Pinned group's dashed row (one unit; two at Compact, where its sentence wraps).</summary>
    public required float EmptyRow { get; init; }

    /// <summary>A row tile's span (columns, units).</summary>
    public required (byte Cols, byte Rows) S { get; init; }

    /// <summary>A card's span.</summary>
    public required (byte Cols, byte Rows) M { get; init; }

    /// <summary>A double card's span.</summary>
    public required (byte Cols, byte Rows) L { get; init; }

    /// <summary>The narrowest an open tile is (columns): its options' fields want two columns where there are two.</summary>
    public required byte OpenMinCols { get; init; }

    /// <summary>The shortcuts grid (owner 2026-11-16, "one grid, edit inside it"): its columns (a floor-fit inside the tier: 2 at Compact, 3 at
    /// Standard, 4 at Huge), its row (a 48 cover in a 64 row), the pips' row under it while it pages.</summary>
    public required WinUiGridBand ShortcutCols { get; init; }

    /// <inheritdoc cref="ShortcutCols"/>
    public required float ShortcutUnit { get; init; }

    /// <inheritdoc cref="ShortcutCols"/>
    public required float ShortcutPipsH { get; init; }

    /// <summary>The ⋯ corner every form reserves (a 32 button and its 4 inset; 40 at Compact, the target floor).</summary>
    public required float MoreBox { get; init; }

    /// <summary>A live candidate's preview box in the add band (width, height).</summary>
    public required float PreviewW { get; init; }

    /// <summary>The add band's candidate card width.</summary>
    public required float CandidateW { get; init; }

    /// <summary>The page's columns at a content width.</summary>
    public int ColumnsAt(float width) => Columns.Columns(width, Gap);

    /// <summary>The pack metrics at a content width.</summary>
    public PackMetrics Metrics(float width) => new(ColumnsAt(width), Unit, Gap, GroupGap);
}

/// <summary>WinUI's board records.</summary>
public static class WinUiHomeBoardTiers
{
    /// <summary>The record for <paramref name="facts"/> (Standard's where no frame resolved the tiers).</summary>
    public static WinUiHomeBoardLayout Of(in TierFacts facts) => For(facts.Policy is null ? WidthTier.Standard : facts.Content);

    /// <summary>The record for content tier <paramref name="content"/> (4K reads Huge's).</summary>
    public static WinUiHomeBoardLayout For(WidthTier content) => (WidthTier)Math.Min((int)content, (int)WidthTier.Huge) switch
    {
        WidthTier.Compact => Base with { Tier = WidthTier.Compact, Columns = WinUiGridBand.Fixed(1), EmptyRow = 2f * UnitH + GapW, MoreBox = 40f, ShortcutsHead = 96f, FaceSwitchH = 40f,
                                         ShortcutCols = WinUiGridBand.Fixed(2) },
        WidthTier.Small => Base with { Tier = WidthTier.Small, Columns = new WinUiGridBand(260f, 2), ShortcutCols = new WinUiGridBand(220f, 3) },
        WidthTier.Standard => Base with { Tier = WidthTier.Standard, Columns = new WinUiGridBand(240f, 4), ShortcutCols = new WinUiGridBand(280f, 3) },
        _ => Base with { Tier = WidthTier.Huge, Columns = new WinUiGridBand(280f, 6), ShortcutCols = new WinUiGridBand(300f, 4) },
    };

    const float UnitH = 72f, GapW = 12f;

    static readonly WinUiHomeBoardLayout Base = new()
    {
        Tier = WidthTier.Standard, Columns = new WinUiGridBand(240f, 4),
        Unit = UnitH, Gap = GapW, GroupGap = 20f, GroupHead = 34f, ShortcutsHead = 56f, FaceSwitchH = 32f, EmptyRow = UnitH,
        S = (1, 1), M = (1, 3), L = (2, 3), OpenMinCols = 2, MoreBox = 32f,
        PreviewW = 220f, CandidateW = 252f,
        ShortcutCols = new WinUiGridBand(280f, 3), ShortcutUnit = 64f, ShortcutPipsH = 24f,
    };
}

/// <summary>The constants of WinUI's board views.</summary>
public static class WinUiHomeBoardRules
{
    /// <summary>A tile's plate corner (the WinUI card's 8).</summary>
    public const float Corner = 8f;

    /// <summary>A tile's inner padding (12; 16 on a card).</summary>
    public const float TilePad = 12f, CardPad = 16f;

    /// <summary>A row tile's cover (48) and a card's cover at its top (the card's width less its padding).</summary>
    public const float RowArt = 48f;

    /// <summary>The ⋯ button's own edge and its inset from the tile's corner.</summary>
    public const float MoreEdge = 32f, MoreInset = 4f;

    /// <summary>The lane a card's content leaves free at its end for the ⋯ (Compact's 40 box and its inset): edit tools never sit on content.</summary>
    public const float MoreReserve = 44f;

    /// <summary>A card's cover (96) and a double card's (the card's height less its padding: 216).</summary>
    public const float CardArt = 96f, WideArt = 216f;

    /// <summary>The list rows a card (3) and a double card (4) show under their head.</summary>
    public const int CardRows = 3, WideRows = 4;

    /// <summary>A dashed outline's runs (the empty row, the drop slot).</summary>
    public const float DashOn = 4f, DashOff = 4f;

    /// <summary>A choice with fewer values than this is vertical RadioButtons; from it on, a ComboBox (Learn: RadioButtons under five, ComboBox from five).</summary>
    public const int RadioMax = 5;

    /// <summary>"/" (VK_OEM_2): the add band's search.</summary>
    public const int SlashKey = 191;

    /// <summary>The add band opening and folding: its height eases (the engine's flow reveal), the board under it rides it; reduced motion fades.</summary>
    public static LayoutTransition BandReveal => new(TransitionChannels.Size, TransitionDynamics.Tween(WinUiDiscoveryMotion.ReflowMs, Easing.SmoothOut),
        Size: SizeMode.FlowReveal);

    /// <summary>The band under reduced motion: the same height change, as a short fade with no travel.</summary>
    public static LayoutTransition BandFade => new(TransitionChannels.Size, TransitionDynamics.Tween(ReducedMs, Easing.SmoothOut), Size: SizeMode.FlowReveal);

    /// <summary>Reduced motion's fade.</summary>
    public const float ReducedMs = 120f;

    /// <summary>The pin mark on a card whose entity is on Home (12, the meta line's glyph).</summary>
    public const float PinMarkGlyph = 12f;

    /// <summary>Half (a round cover's corner is half its edge).</summary>
    public const float Half = 0.5f;

    /// <summary>The ring around a tile just added: 2 DIP, shown for 1.8 s.</summary>
    public const float RingW = 2f;
    public const int RingMs = 1800;

    /// <summary>The add band: its head row (40), the candidates' row (the preview, the name, a line, the sizes, Pin), its padding and its foot.</summary>
    public const float BandHeadH = 40f, BandPadX = 16f, BandPadTop = 6f, BandPadBottom = 12f, BandFoot = 20f, BandRowGap = 12f;

    /// <summary>A candidate's preview height (a card's form at the candidate's width).</summary>
    public const float PreviewH = 122f;

    /// <summary>The search box: at most 300 wide, 32 tall.</summary>
    public const float SearchMaxW = 300f, SearchH = 32f;

    /// <summary>The page offset past which the first-run tip waits (the head row is leaving the viewport).</summary>
    public const double TipScrollMax = 48d;

    /// <summary>The first-run tip waits this many presented frames after the mount (the page's entrance has played): about 600 ms at 60 Hz, the
    /// concept's delay, counted in frames so a slow first paint cannot pull it into the entrance.</summary>
    public const int TipDelayFrames = 36;

    /// <summary>The search result count is spoken once typing pauses this long (not on every keystroke).</summary>
    public const float SpeakCountMs = 600f;

    /// <summary>The teaching tip's width.</summary>
    public const float TipW = 330f;

    /// <summary>An open tile's options: a field row (a label over its control) and the head ("Customize …", Done).</summary>
    public const float OptionsHeadH = 48f, OptionRowH = 64f, OptionRadioRowH = 32f;

    /// <summary>Up next and Recents list rows inside a card: a 40 row with a 32 cover.</summary>
    public const float ListRowH = 40f, ListArt = 32f;

    /// <summary>The widget head inside a card: a 16 glyph, the 14 semibold title, a 12 line (32 tall).</summary>
    public const float WidgetHeadH = 32f, WidgetGlyph = 16f;

    /// <summary>The now-playing card's progress bar (3) and its transport buttons (32).</summary>
    public const float ProgressH = 3f, TransportBox = 32f;

    /// <summary>A full row's measured height moves its box only when it differs from the packed height by more than half a DIP.</summary>
    public const float ExtentSlop = 0.5f;

    /// <summary>The blank cards of a loading shelf's placeholder.</summary>
    public const int SeedCards = 6;

    /// <summary>A full-row tile's estimate where nothing better is known (a shelf's header and one row of covers).</summary>
    public const float FullFallback = 320f;

    /// <summary>The theme's auto size of a source (spec 4.3).</summary>
    public static HomeSize AutoOf(HomeSourceDef? source) => source?.CanonicalAuto is { } a && a != HomeSize.Auto ? a : HomeSize.S;

    /// <summary>The size an entry packs at: its own, else the theme's auto. The history tile of the shortcuts grid is always a row tile.</summary>
    public static HomeSize SizeOf(BoardEntry e) => HomeShortcuts.IsHistoryCell(e) ? HomeSize.S : e.Size != HomeSize.Auto ? e.Size : AutoOf(e.Source);

    /// <summary>The form a packed box can carry (spec 4.2): the class is the floor; a card the fill made two columns wide draws the wide form, a
    /// row tile that got three units draws the card. A full row is never decided here: it always draws its full form.</summary>
    public static HomeTileForm FormOf(in PackBox box, WinUiHomeBoardLayout lay, int columns)
    {
        if (box.Rows >= lay.M.Rows - 0.01f) return box.Cols >= lay.L.Cols ? HomeTileForm.L : HomeTileForm.M;
        return HomeTileForm.S;
    }

    /// <summary>The pack entry of a board entry. <paramref name="fullExtent"/> is a full row's height (measured, else estimated);
    /// <paramref name="openExtent"/> the open tile's (0: not open).</summary>
    public static PackEntry EntryOf(BoardEntry e, WinUiHomeBoardLayout lay, float fullExtent, float openExtent, bool emptyGroup)
    {
        switch (e.Kind)
        {
            case BoardEntryKind.GroupHead:
                return new PackEntry(PackEntryKind.GroupHead, Extent: e.GroupId == HomeDocs.Pinned ? lay.ShortcutsHead : lay.GroupHead);
        }
        if (IsHidden(e)) return new PackEntry(PackEntryKind.Tile, Flags: PackFlags.Hidden);
        var size = SizeOf(e);
        var flags = e.Size == HomeSize.Auto ? PackFlags.Auto : PackFlags.None;
        if (size == HomeSize.Full)
            return new PackEntry(PackEntryKind.Band, Flags: flags, Extent: MathF.Max(fullExtent, openExtent));
        var (c, r) = size switch { HomeSize.L => lay.L, HomeSize.M => lay.M, _ => lay.S };
        if (openExtent > 0f) flags |= PackFlags.Open;
        return new PackEntry(PackEntryKind.Tile, c, r, flags, openExtent, lay.OpenMinCols);
    }

    /// <summary>A hidden entry (a placed section the answer lacks, a hidden feed section) keeps its place and takes no room.</summary>
    public static bool IsHidden(BoardEntry e) => e.Hidden;

    /// <summary>The open tile's height: the head and one row per option (a radio group grows by its values under five), plus the live toggle.</summary>
    public static float OpenExtent(BoardEntry e, HomeSourceDef? source, WinUiHomeBoardLayout lay)
    {
        if (source is null) return 0f;
        float h = OptionsHeadH + CardPad;
        foreach (var o in source.Options)
            h += o.Kind == OptionKind.Choice && o.Values.Length < 5 ? OptionRadioRowH * o.Values.Length + OptionRowH - OptionRadioRowH : OptionRowH;
        return MathF.Max(h, lay.M.Rows * lay.Unit + (lay.M.Rows - 1) * lay.Gap);
    }
}
