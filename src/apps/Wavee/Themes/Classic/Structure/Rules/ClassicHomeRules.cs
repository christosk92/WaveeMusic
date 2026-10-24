// ── Themes/Classic/Structure/Rules/ClassicHomeRules.cs — the numbers of Classic's Home (themes plan 6.1, T10a1; P22) ─────────
//
// What `Home\Zones.UI.cs` (lines 88-115, the plan's P22), `Facet.UI.cs`, `Daylist.UI.cs`, and `SectionScreen.UI.cs` held as
// file-level constants beside the pictures they size, moved verbatim with those pictures: the pinned facet band's three heights, the chapter's
// sticky geometry (the header row, the gap, the clip line and its feather), the grid geometry per zone body (the stock auto-fill's minimum
// column and column cap), the Recents tiles' pieces, the 2-span lead cell, the daylist card's floor, art focus and copy insets, the drill
// page's card minimum. They are data, not decisions (WVTH003 allows literals only in `Rules\**`); the DECISIONS
// stay core and pure (`FacetForm`, `DaylistForm`, `DaypartTimeline`, `ShelfLead`, `RecentsPlan`, `FacetDimPlan`...), and the views call them.
//
// Public, because the Recents grid's fact (632 DIP fits three columns, 836 four) is pinned by `ControlsTests` over `RecentsMinCol`.

using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee.Themes.Classic;

/// <summary>The constants of Classic's Home views.</summary>
public static class ClassicHomeRules
{
    // ── the facet row: the pinned band (the three numbers the page and the zones share) ───────────────────────────────

    /// <summary>The selector HStack's height: <c>R.Type.FacetTitleStyle</c>'s item height (the prototype's 40-px facet
    /// row — a 28/36 display word in a 40-px hover plate, no pill slot). The row is set to exactly this and centres the
    /// bar in it, so the constant is authoritative for the sticky clip inset rather than a measured value.</summary>
    public const float SelectorH = 40f;

    /// <summary>The words' line: the selector's height and the stock SelectorBar's own 4-DIP padding above and below (SelectorBarPadding 0,4), the
    /// height its scrolling viewport is given (a horizontal scroller does not take its content's height), centred on the row as the bar was.</summary>
    public const float WordsLineH = SelectorH + 2f * 4f;

    /// <summary>The busy bar's slot: WinUI's 3-px ProgressBar, part of the pinned band so a switch started deep in the
    /// page still shows its progress.</summary>
    public const float BarH = 3f;

    /// <summary>The pinned band's height (selector row + busy bar) — the page's <c>StickyClip</c> inset and the base of
    /// every chapter header's stick inset.</summary>
    public const float FacetRowH = SelectorH + BarH;

    /// <summary>How far below the viewport's top edge the band pins: a breath so the pinned words never sit flush
    /// against the page surface's rounded top (the page ScrollView draws no top edge cue — its own StickyClip + WhileStuck
    /// feathers do the dissolving, the Artist page's precedent).</summary>
    public static float StuckInset => Spacing.M;

    /// <summary>The pinned band's LOWER edge in the viewport — the page content's clip line and every chapter header's
    /// stick inset. The one number all three share.</summary>
    public static float StuckBottom => StuckInset + FacetRowH;

    /// <summary>The page column's <c>ScrollScope</c> name: the band pins against it (so the pin holds for the whole page
    /// and not for the facet row's own column, which is where a scope-less <c>.Sticky</c> would release).</summary>
    public const string PageScope = "home:page";

    /// <summary>The failure bar's slide-in distance (it enters 8 DIP above its rest).</summary>
    public const float FailureEnterDy = -8f;

    // ── the chapter geometry (the sticky idiom's three numbers) ───────────────────────────────────────────────────────

    /// <summary>The pinned header row's height: the 20/28 module title with a 32-tall stock tools control beside it.</summary>
    public const float HeaderH = Design.Size.ControlH;

    /// <summary>The breath between a header and its body — INSIDE the header's sticky box, so the body's clip line sits
    /// exactly under it.</summary>
    public static float HeaderGap => Spacing.M;

    /// <summary>The feather the body dissolves through while its clip is engaged (the shelf fade rung, as Browse).</summary>
    public static float ClipFadeBand => R.Space.FadeShelf;

    /// <summary>The viewport line a zone body is cut at while its header is pinned.</summary>
    public static float BodyClipInset => StuckBottom + HeaderH + HeaderGap;

    // ── the grid geometry per body (stock auto-fill: MinColWidth × MaxColumns; the width picks the count) ───────────────

    /// <summary>The Recents grid's minimum column: 200, so the owner's 632-DIP page fits three (3·200 + 2·12 = 624 ≤ 632
    /// → 5 played tiles + the history tile) and four arrive at 836.</summary>
    public const float RecentsMinCol = 200f;
    public const float ListMinCol = 320f;
    public const float ClusterMinCol = 300f;
    public const float TilesMinCol = 220f;
    public const int RecentsMaxCols = 4;
    public const int ListMaxCols = 2;
    public const int ContinueMaxCols = 3;
    public const int ClusterMaxCols = 3;
    public const int TilesMaxCols = 4;
    public const int ChartsMaxCols = 3;

    /// <summary>A Recents tile's row height, a cluster row's art, a browse tile's art plate.</summary>
    public const float RowH = 64f;
    public const float ClusterArt = 40f;
    public const float TileArt = Design.Size.Thumb48;

    /// <summary>How many release rows, cluster cards and cluster rows a body shows.</summary>
    public const int ReleaseRowsMax = 6;
    public const int ClusterMax = 6;
    public const int ClusterRows = 3;

    /// <summary>The Recents cell's equalizer height, the history tile's 7-day strip box, the header tools' divider.</summary>
    public const float EqualizerH = 12f;
    public const float HistoryStripW = 56f;
    public const float HistoryStripH = 14f;
    public const float ToolsDividerH = 16f;

    /// <summary>The history strip's bar gap and floor.</summary>
    public const float SparkGap = 2f;
    public const float SparkMinBar = 2f;

    /// <summary>The 2-span lead cell of a cover shelf (E21/E22): item 0 spans two columns once the shelf has three —
    /// the lead plus one square — so the hero survives a narrow page (the right panel open); at two columns it would
    /// fill the whole row, so it mounts as a plain square there.</summary>
    public const int LeadSpan = 2;
    public const int LeadMinColumns = 3;

    // ── the daylist card ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The decode target for the art: it cover-fills the whole card (~1000 DIP on a wide Home), so the square
    /// hint is sized to that, not to a column.</summary>
    public const int DaylistArtDecodePx = 1024;

    /// <summary>The art's focal line: a little above centre, where a daylist header's subject sits.
    /// (<c>Controls.CoverFill</c> takes no horizontal focus, so the crop stays centred on that axis.)</summary>
    public const float DaylistArtFocusY = 0.4f;

    /// <summary>The card's floor height (the prototype's 320 hero).</summary>
    public const float DaylistCardMinHeight = 320f;

    /// <summary>The copy column's insets, which are the card's own (the card has no padding, so the art reaches its
    /// edges): 32 from the left edge, the eyebrow 32 below the top, 12 from the right edge (where a narrow card's copy
    /// ends), the timeline labels 24 above the foot.</summary>
    public static Edges4 DaylistCopyPadding => new(Spacing.XXXL, Spacing.XXXL, Spacing.M, Spacing.XXL);

    /// <summary>The tier forms' pieces (`C-Home-Compact`, `C-Home-Huge`): the Compact stack's copy rises this far over its art; the Huge hero's clock is a
    /// 440-wide dark plate (padding 14 / 12) inset 24 from the card's corner; the Compact and Small clock rows' gap.</summary>
    public const float StackedOverlap = 28f, ClockPlateW = 440f, ClockPlateInset = 24f;

    /// <summary>Compact's stacked daylist: its vertical veil peaks where the copy rises over the art band (<see cref="StackedOverlap"/> above its foot,
    /// less a line of slack) and holds to the foot, so the eyebrow is on the full veil. NaN for a band with no height.</summary>
    public static float StackedVeilHold(float artH) => artH <= StackedOverlap ? float.NaN : (artH - 2f * StackedOverlap) / artH;
    /// <summary>The Huge clock plate's fill. The plate lies mostly on the card's veil, which follows the kind (graded from the art toward the page's
    /// ground), so the plate follows the kind too and its inks stay the page's: the board's dark plate (black 26 %, C-Home-Huge) in dark, the card's
    /// frosted fill in light, where black 26 % over the light veil left a mid-grey that neither ink read on (2.4 : 1).</summary>
    public static ColorF ClockPlateFill => Tok.Theme == ThemeKind.Light ? Tok.FillCardDefault : ColorF.FromRgba(0, 0, 0) with { A = 0.26f };
    public static Edges4 ClockPlatePadding => new(14f, 12f, 14f, 12f);

    /// <summary>At most this many tag links on the description line.</summary>
    public const int DaylistMaxTags = 6;

    /// <summary>The countdown ring's size (the stock ring's minimum).</summary>
    public const float RingSize = FluentGpu.Controls.ProgressRing.MinSize;

    // ── the "See all" drill page ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The grid's MIN cell width (<c>RepeatLayout.GridFit</c> derives the column count from it and the engine's own measured
    /// cross size — no app-side width/tier math).</summary>
    public const float SectionCardMinWidth = 176f;

    /// <summary>The gap between the "N items" line and the grid.</summary>
    public const float SectionBodyGap = 4f;
}
