// ── Themes/Classic/Structure/Views/Home/ClassicHomeZones.Rhythm.cs — the Home rhythm templates in Classic (solution A, #a-classic) ─────
//
// Owner 2026-11-16: "go with A, the issue is not shorter page its mono all same stuff". Each section's template follows what it holds and
// no two neighbours share one (SectionChooser + SectionRhythm). The four templates A adds, from Classic's house surface and grids:
//
//   LeadGrid       a card on the card fill (no stroke: Classic's cards have none) — the lead's header art filling it, its name over two caption
//                  lines, Play at the end — beside a grid of the next covers (half each; Compact stacks the lead over the grid at 16 : 8)
//   CompactGrid    small tiles, the cover beside the text (the Recently played tile), two to five columns, two rows
//   ArtistCircles  one row of round covers, the name centred under each (8 at Standard, fewer narrower)
//   CappedShelf    one row of cards with two title lines (6 at Standard, fewer narrower), "See all N" in the header instead of paging
//
// Every count is the content tier's `ClassicHomeLayout` (LeadGrid, Compact, Circles, Capped); every grid shows columns x rows cards (never a
// ragged row). A crossing re-fits each card in place (Refit).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.HomeUi;
using Wavee.Theming;

namespace Wavee.Themes.Classic;

internal static partial class ClassicHomeZones
{
    // ── shapes ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    static SurfaceShape GridCardShape => Shape.Grid;
    static SurfaceShape CircleShape => Shape.Grid with { CaptionLines = 0 };
    static SurfaceShape CappedShape => Shape.Grid with { TitleLines = ClassicHomeRules.CappedTitleLines };
    static SurfaceShape CompactShape(ClassicHomeLayout layout) => Shape.RowTile with { ArtEdge = layout.CompactArt, MinHeight = layout.CompactRowH };

    // ── the cells ───────────────────────────────────────────────────────────────────────────────────────────────────────

    static Element CoverCard(HomeCard c, string prefix, SurfaceShape shape, IOverlayService? host, bool circular, bool caption)
    {
        string sub = caption ? HomeCards.PlainText(c.Subtitle) : "";
        var data = RhythmCards.Data(in c, sub.Length > 0 ? Meta(sub) : null, host, circular);
        return new BoxEl
        {
            Key = prefix + c.Uri, Direction = 1, MinWidth = 0f, Layout = ClassicTierMotion.Refit,
            Children = [Controls.Surface(data, shape)],
        };
    }

    // ── LeadGrid ────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>"Made For": the first card as the lead (its header art, name, line and Play) beside (or over) a grid of the next ones.</summary>
    static Element LeadGrid(Zone zone, IOverlayService? overlay, ClassicHomeLayout layout)
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
            Children = [new GridEl { Columns = RhythmCards.Stars(band.Cols), ColGap = Spacing.M, RowGap = Spacing.M, Children = cells }],
        };
        return new BoxEl
        {
            Key = "lead-grid:" + zone.Key, Direction = (byte)(beside ? 0 : 1), Gap = Spacing.M, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
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
            ZStack = true, MinWidth = 0f, MinHeight = beside ? ClassicHomeRules.LeadArtMin : 0f, Grow = beside ? 1f : 0f,
            AspectRatio = beside ? float.NaN : ClassicHomeRules.LeadArtAspect, ClipToBounds = true,
            Cursor = CursorId.Hand, OnClick = () => HomeCardNav.Open(in cc),
            Children = [Controls.CoverFill(c.HeaderImageUrl ?? c.ImageUrl, 0f, ClassicHomeRules.LeadArtDecodePx)],
        };
        var copy = new BoxEl
        {
            Direction = 0, Height = ClassicHomeRules.LeadCopyH, Shrink = 0f, MinWidth = 0f, AlignItems = FlexAlign.End, Gap = ClassicHomeRules.LeadCopyGap,
            Padding = new Edges4(ClassicHomeRules.LeadCopyPadX, ClassicHomeRules.LeadCopyPadTop, ClassicHomeRules.LeadCopyPadX, ClassicHomeRules.LeadCopyPadBottom),
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
            Fill = Tok.FillCardDefault,
            Grow = beside ? 1f : 0f, Shrink = 1f, Basis = beside ? 0f : float.NaN, Layout = ClassicTierMotion.Refit,
            // Its edges meet the covers beside it: a fluid cover card stands its plate inset above the cover and under the labels
            // (it has no shelf gutter).
            Margin = new Edges4(0f, LeadInsetTop, 0f, SurfaceGeometry.ShelfPlatePad),
            Children = [art, copy],
        };
    }

    static float LeadInsetTop => SurfaceGeometry.ShelfPlatePad;

    // ── the capped grids: CompactGrid, ArtistCircles, CappedShelf ───────────────────────────────────────────────────────

    /// <summary>A grid that shows columns x rows of the zone's cards: the columns are the band's fit at the measured width (a form-gated
    /// responsive box: a width inside the same count rebuilds nothing), the cards are star tracks.</summary>
    static Element CappedGrid(Zone zone, ClassicCappedBand band, float gap, float rowH, Func<HomeCard, Element> cell)
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
            return new GridEl
            {
                Columns = RhythmCards.Stars(c), ColGap = gap, RowGap = gap, RowHeight = rowH, Children = cells,
            };
        }, fallback: first);
    }

    static Element CompactGrid(Zone zone, IOverlayService? overlay, ClassicHomeLayout layout)
    {
        var host = HostOf(overlay);
        var shape = CompactShape(layout);
        return CappedGrid(zone, layout.Compact, Spacing.S, layout.CompactRowH, c =>
        {
            string sub = HomeCards.PlainText(c.Subtitle);
            var data = RhythmCards.Data(in c, sub.Length > 0 ? Meta(sub) : null, host, circular: c.Kind == HomeCardKind.Artist);
            return new BoxEl
            {
                Key = "compact-tile:" + c.Uri, Direction = 1, MinWidth = 0f, Layout = ClassicTierMotion.Refit,
                Children = [Controls.Surface(data, shape)],
            };
        });
    }

    static Element ArtistCircles(Zone zone, IOverlayService? overlay, ClassicHomeLayout layout)
    {
        var host = HostOf(overlay);
        return CappedGrid(zone, layout.Circles, Spacing.M, float.NaN,
            c => CoverCard(c, "circle:", CircleShape, host, circular: true, caption: false));
    }

    static Element CappedShelf(Zone zone, IOverlayService? overlay, ClassicHomeLayout layout)
    {
        var host = HostOf(overlay);
        return CappedGrid(zone, layout.Capped, Spacing.M, float.NaN,
            c => CoverCard(c, "capped:", CappedShape, host, circular: c.Kind == HomeCardKind.Artist, caption: true));
    }
}
