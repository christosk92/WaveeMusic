// ── Themes/WinUi/Structure/Views/Discovery/Home/WinUiHomeZones.Rhythm.cs — the Home rhythm templates in WinUI (solution A, #a-winui) ─────
//
// Owner 2026-11-16: "go with A, the issue is not shorter page its mono all same stuff". Each section's template follows what it holds and
// no two neighbours share one (SectionChooser + SectionRhythm). The four templates A adds, built from the house surface and grids:
//
//   LeadGrid       a card (the lead's header art filling it, its name 20/28 over two caption lines, Play at the end) beside a grid of the next
//                  covers (half each; Compact stacks the lead over the grid at 16 : 8)                                  tools: See all N
//   CompactGrid    small tiles, the cover beside the text (the card tile of Jump back in), two to five columns, two rows   tools: See all N
//   ArtistCircles  one row of round covers, the name centred under each (8 at Standard, fewer narrower)                    tools: See all N
//   CappedShelf    one row of cards with two title lines (6 at Standard, fewer narrower), "See all N" instead of paging   tools: See all N
//
// Every count is the content tier's `WinUiHomeLayout` (LeadGrid, Compact, Circles, Capped); every grid shows columns x rows cards (never a
// ragged row) and every body's height is known from its width (`ChapterExtent`). A width change FLIPs each card to its new track (Shove).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.HomeUi;
using Wavee.Theming;

namespace Wavee.Themes.WinUi;

internal static partial class WinUiHomeZones
{
    // ── shapes ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    static SurfaceShape GridCardShape => Shape.Grid with { Flush = true };
    static SurfaceShape CircleShape => Shape.Grid with { Flush = true, CaptionLines = 0 };
    static SurfaceShape CappedShape => Shape.Grid with { Flush = true, TitleLines = WinUiHomeRules.CappedTitleLines };
    static SurfaceShape CompactShape(WinUiHomeLayout layout) => Shape.RowTile with { ArtEdge = layout.CompactArt, MinHeight = layout.CompactRowH };

    /// <summary>A fluid cover card's height at <paramref name="cellW"/> in <paramref name="shape"/>: the surface's own extent maths less the shelf
    /// gutter a grid cell does not stand (only a fixed-width shelf card wears it).</summary>
    static float CardExtent(in SurfaceShape shape, float cellW) => SurfaceGeometry.StackExtent(in shape, cellW, 1f) - SurfaceGeometry.ShelfGutterTop;

    // ── the cells ───────────────────────────────────────────────────────────────────────────────────────────────────────

    static Element CoverCard(HomeCard c, string prefix, SurfaceShape shape, IOverlayService? host, bool circular, bool caption)
    {
        string sub = caption ? HomeCards.PlainText(c.Subtitle) : "";
        var data = RhythmCards.Data(in c, sub.Length > 0 ? Meta(sub) : null, host, circular);
        return new BoxEl
        {
            Key = prefix + c.Uri, Direction = 1, MinWidth = 0f, Layout = WinUiDiscoveryMotion.Shove,
            Children = [Controls.Surface(data, shape)],
        };
    }

    // ── LeadGrid ────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>"Made For": the first card as the lead (its header art, name, line and Play) beside (or over) a grid of the next ones.</summary>
    static Element LeadGrid(Zone zone, IOverlayService? overlay, WinUiHomeLayout layout)
    {
        var cards = RhythmCards.Usable(zone.Items);
        if (cards.Count == 0) return new BoxEl();
        var band = layout.LeadGrid;
        bool beside = band.Beside;
        var host = HostOf(overlay);
        int n = RhythmCards.Shown(band.Cols, band.Rows, cards.Count - 1);
        var cells = new Element[n];
        for (int i = 0; i < n; i++) cells[i] = CoverCard(cards[i + 1], "lead-grid-card:", GridCardShape, host, circular: cards[i + 1].Kind == HomeCardKind.Artist, caption: true);
        Element grid = new BoxEl
        {
            Direction = 1, MinWidth = 0f, Grow = beside ? 1f : 0f, Shrink = 1f, Basis = beside ? 0f : float.NaN, AlignItems = FlexAlign.Stretch,
            Children = [GridFill.Build(cells, GridFill.Columns(n, band.Cols), WinUiHomeRules.CardGap, WinUiHomeRules.CardGap)],
        };
        return new BoxEl
        {
            Key = "lead-grid:" + zone.Key, Direction = (byte)(beside ? 0 : 1), Gap = WinUiHomeRules.CardGap, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
            Children = [LeadCard(cards[0], beside), grid],
        };
    }

    /// <summary>The lead card: its header art (its cover where it has none) fills the card above a fixed copy band, so the card is as tall as the
    /// grid beside it (or 16 : 8 over its copy, stacked).</summary>
    static Element LeadCard(HomeCard c, bool beside)
    {
        var cc = c;
        string line = HomeCards.PlainText(c.Subtitle);
        var art = new BoxEl
        {
            ZStack = true, MinWidth = 0f, MinHeight = beside ? WinUiHomeRules.LeadArtMin : 0f, Grow = beside ? 1f : 0f,
            AspectRatio = beside ? float.NaN : WinUiHomeRules.LeadArtAspect, ClipToBounds = true,
            Cursor = CursorId.Hand, OnClick = () => HomeCardNav.Open(in cc),
            Children = [Controls.CoverFill(c.HeaderImageUrl ?? c.ImageUrl, 0f, WinUiHomeRules.LeadArtDecodePx)],
        };
        var copy = new BoxEl
        {
            Direction = 0, Height = WinUiHomeRules.LeadCopyH, Shrink = 0f, MinWidth = 0f, AlignItems = FlexAlign.End, Gap = WinUiHomeRules.LeadCopyGap,
            Padding = new Edges4(WinUiHomeRules.LeadCopyPadX, WinUiHomeRules.LeadCopyPadTop, WinUiHomeRules.LeadCopyPadX, WinUiHomeRules.LeadCopyPadBottom),
            Children =
            [
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f,
                    Children =
                    [
                        new BoxEl
                        {
                            Direction = 0, MinWidth = 0f, Cursor = CursorId.Hand, Role = AutomationRole.Hyperlink, Focusable = true,
                            OnClick = () => HomeCardNav.Open(in cc),
                            Children = [R.Type.ModuleHeader(c.Title) with { MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f }],
                        },
                        Ui.Caption(line.Length > 0 ? line : " ").Secondary() with { MaxLines = 2, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                    ],
                },
                Button.Create(Loc.Get(Strings.Home.Play), () => HomeCardNav.Play(in cc), ButtonAppearance.Accent, ControlSize.Medium, Icons.Play) with { Shrink = 0f },
            ],
        };
        return new BoxEl
        {
            Key = "lead-card:" + c.Uri, Direction = 1, MinWidth = 0f, ClipToBounds = true, Corners = Radii.CardAll,
            Fill = Tok.FillCardDefault, BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f,
            Grow = beside ? 1f : 0f, Shrink = 1f, Basis = beside ? 0f : float.NaN, Layout = WinUiDiscoveryMotion.Shove,
            // Its edges meet the covers beside it: a fluid cover card stands its plate inset above the cover and under the labels
            // (it has no shelf gutter).
            Margin = new Edges4(0f, LeadInsetTop, 0f, SurfaceGeometry.ShelfPlatePad),
            Children = [art, copy],
        };
    }

    static float LeadInsetTop => SurfaceGeometry.ShelfPlatePad;

    static float LeadGridExtent(Zone zone, WinUiHomeLayout layout, float width)
    {
        int count = RhythmCards.Usable(zone.Items).Count;
        if (count == 0) return 0f;
        var band = layout.LeadGrid;
        int n = RhythmCards.Shown(band.Cols, band.Rows, count - 1);
        float gap = WinUiHomeRules.CardGap;
        float gridW = band.Beside ? (width - gap) / 2f : width;
        int cols = GridFill.Columns(n, band.Cols);
        int rows = GridFill.Rows(n, cols);
        float card = CardExtent(GridCardShape, GridFill.CellWidth(gridW, cols, gap));
        float grid = rows == 0 ? 0f : rows * card + (rows - 1) * gap;
        float inset = LeadInsetTop + SurfaceGeometry.ShelfPlatePad;
        if (band.Beside) return MathF.Max(grid, WinUiHomeRules.LeadArtMin + WinUiHomeRules.LeadCopyH + inset);
        float lead = width / WinUiHomeRules.LeadArtAspect + WinUiHomeRules.LeadCopyH + inset;
        return rows == 0 ? lead : lead + gap + grid;
    }

    // ── the capped grids: CompactGrid, ArtistCircles, CappedShelf ───────────────────────────────────────────────────────

    /// <summary>A grid that shows columns x rows of the zone's cards: the columns are the band's fit at the measured width (a form-gated
    /// responsive box: a width inside the same count rebuilds nothing), the cards are star tracks.</summary>
    static Element CappedGrid(Zone zone, WinUiCappedBand band, float gap, float rowH, Func<HomeCard, Element> cell)
    {
        var cards = RhythmCards.Usable(zone.Items);
        if (cards.Count == 0) return new BoxEl();
        var cols = band.Columns;
        float first = MathF.Max(cols.MinCol, 1f) * cols.MaxCols + gap * (cols.MaxCols - 1);
        return Responsive.Of("rhythm:" + zone.Key, w => cols.Columns(w, gap), c =>
        {
            int n = RhythmCards.Shown(c, band.Rows, cards.Count);
            var cells = new Element[n];
            for (int i = 0; i < n; i++) cells[i] = cell(cards[i]);
            return GridFill.Build(cells, GridFill.Columns(n, c), gap, gap, rowH);
        }, fallback: first);
    }

    static Element CompactGrid(Zone zone, IOverlayService? overlay, WinUiHomeLayout layout)
    {
        var host = HostOf(overlay);
        var shape = CompactShape(layout);
        return CappedGrid(zone, layout.Compact, WinUiHomeRules.CompactGap, layout.CompactRowH, c =>
        {
            string sub = HomeCards.PlainText(c.Subtitle);
            var data = RhythmCards.Data(in c, sub.Length > 0 ? Meta(sub) : null, host, circular: c.Kind == HomeCardKind.Artist);
            return new BoxEl
            {
                Key = "compact-tile:" + c.Uri, Direction = 1, MinWidth = 0f, Layout = WinUiDiscoveryMotion.Shove,
                Children = [Controls.Surface(data, shape)],
            };
        });
    }

    static Element ArtistCircles(Zone zone, IOverlayService? overlay, WinUiHomeLayout layout)
    {
        var host = HostOf(overlay);
        return CappedGrid(zone, layout.Circles, WinUiHomeRules.CardGap, float.NaN,
            c => CoverCard(c, "circle:", CircleShape, host, circular: true, caption: false));
    }

    static Element CappedShelf(Zone zone, IOverlayService? overlay, WinUiHomeLayout layout)
    {
        var host = HostOf(overlay);
        return CappedGrid(zone, layout.Capped, WinUiHomeRules.CardGap, float.NaN,
            c => CoverCard(c, "capped:", CappedShape, host, circular: c.Kind == HomeCardKind.Artist, caption: true));
    }

    /// <summary>A capped grid's height at <paramref name="width"/>: its rows of tiles (a fixed row height) or of cover cards (the card's extent
    /// at the star track's width).</summary>
    static float CappedExtent(Zone zone, WinUiCappedBand band, float gap, float rowH, SurfaceShape shape, float width)
    {
        int count = RhythmCards.Usable(zone.Items).Count;
        if (count == 0) return 0f;
        int fit = band.Columns.Columns(width, gap);
        int n = RhythmCards.Shown(fit, band.Rows, count);
        int cols = GridFill.Columns(n, fit);
        int rows = GridFill.Rows(n, cols);
        float row = float.IsNaN(rowH) ? CardExtent(in shape, GridFill.CellWidth(width, cols, gap)) : rowH;
        return rows * row + (rows - 1) * gap;
    }

    static float RhythmExtent(Zone zone, WinUiHomeLayout layout, float width) => zone.Kind switch
    {
        ZoneKind.LeadGrid => LeadGridExtent(zone, layout, width),
        ZoneKind.CompactGrid => CappedExtent(zone, layout.Compact, WinUiHomeRules.CompactGap, layout.CompactRowH, CompactShape(layout), width),
        ZoneKind.ArtistCircles => CappedExtent(zone, layout.Circles, WinUiHomeRules.CardGap, float.NaN, CircleShape, width),
        ZoneKind.CappedShelf => CappedExtent(zone, layout.Capped, WinUiHomeRules.CardGap, float.NaN, CappedShape, width),
        _ => 0f,
    };
}
