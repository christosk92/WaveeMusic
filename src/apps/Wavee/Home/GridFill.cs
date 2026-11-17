// ── Home/GridFill.cs: a Home grid never leaves an empty slot (owner 2026-11-16, "how can we improve these sections so they dont cause
// empty spaces like this?") ──────────────────────────────────────────────────────────────────────────────────────────────────────────
//
// Every grid-shaped Home body (the preview cards, the mood plates, the release rows, the Browse tiles, the episode rows, and the rhythm
// templates' grids) lays its cards out by two rules, deterministic from (count, width):
//
//   1. One formula: the fewest rows the width allows (rows = ceil(n / fit)) and the columns that spread the cards evenly over them
//      (cols = ceil(n / rows)): 4 at 3 are 2 x 2, 5 at 4 are 3 + 2, 7 at 4 are 4 + 3, 8 at 4 are 4 x 2.
//   2. The last row's cards GROW to fill it (grow-only, like the board's bento): never an empty slot, never a card under its minimum. A
//      lone card in a row of several columns may take a wide form of its own (the theme's: a preview card puts its rows in two columns
//      beside its offer).
//   3. Tall cards (the preview cards) stand two rows at most per PAGE: n cards at fit columns take rowsPerPage = min(2, ceil(n / fit))
//      rows, pages = ceil(n / (rowsPerPage x fit)), spread evenly (each ceil(n / pages)), each page laid out by rules 1 and 2. The pages
//      slide sideways in one PagedShelf of page-wide cells (snapped to a page; swipe, wheel and keys as every shelf), the section keeping
//      the tallest page's height so paging never moves what is under it. One page: no pager. The theme draws the pager (owner 2026-11-16).
//
// The cards stand in ONE wrapping row whatever the column count (a card keeps its parent and its node: a column change re-flows the
// same cards with the theme's motion, it never rebuilds them), each card's width the width's share, in a responsive box keyed by the body.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee;

public static class GridFill
{
    /// <summary>The columns for <paramref name="count"/> cards where <paramref name="fit"/> columns fit the width: the fewest rows the fit
    /// allows, <c>rows = ceil(count / fit)</c>, and the columns that spread the cards evenly over them, <c>ceil(count / rows)</c>; the last row's
    /// cards grow to fill it (4 at 3 are 2 x 2, 5 at 4 are 3 + 2, 6 at 4 are 3 x 2, 7 at 4 are 4 + 3, 8 at 4 are 4 x 2).</summary>
    public static int Columns(int count, int fit)
    {
        fit = Math.Max(1, fit);
        if (count <= 0) return fit;
        int rows = (count + fit - 1) / fit;
        return (count + rows - 1) / rows;
    }


    /// <summary>The rows <paramref name="count"/> cards take at <paramref name="cols"/> columns.</summary>
    public static int Rows(int count, int cols) => count <= 0 ? 0 : (count + Math.Max(1, cols) - 1) / Math.Max(1, cols);

    /// <summary>The cards on the last row when it is not full (0 when every row is).</summary>
    public static int Remainder(int count, int cols) => cols <= 1 ? 0 : count % cols;

    /// <summary>The width of one of <paramref name="cols"/> equal columns <paramref name="gap"/> apart across <paramref name="width"/>.</summary>
    public static float CellWidth(float width, int cols, float gap) => cols <= 1 ? width : (width - (cols - 1) * gap) / cols;

    /// <summary>The width a last-row card takes when <paramref name="rest"/> cards share the row.</summary>
    public static float LastWidth(float width, int rest, float gap) => rest <= 1 ? width : (width - (rest - 1) * gap) / rest;

    /// <summary>A grid that lays its cards out at the width it measures: <paramref name="fit"/> maps the width to the columns that fit it,
    /// <paramref name="build"/> builds the cards at (fit, width) with <see cref="Build"/>. Rebuilt for every width it is laid out at (the
    /// cards' widths are the width's), every card keeping its node: a resize re-flows the same cards.</summary>
    public static Element Of(string key, Func<float, int> fit, Func<int, float, Element> build, float fallback)
        => FluentGpu.Controls.Responsive.Of(key, w => build(fit(w), w), fallback: fallback);

    /// <summary>A grid of <paramref name="count"/> cards at the columns <see cref="Columns"/> picks from the fit.</summary>
    public static Element Responsive(string key, int count, Func<float, int> fit, Func<int, float, Element> build, float fallback)
        => Of(key, fit, (f, w) => build(Columns(count, f), w), fallback);

    /// <summary>The cards at <paramref name="cols"/> columns across <paramref name="width"/>, in ONE wrapping row (so a card keeps its parent,
    /// and its node, whatever the column count): the full rows' cards a column wide, the last row's cards sharing its width (grow-only;
    /// <paramref name="lone"/>, when given, draws a lone last card in its wide form). <paramref name="rowH"/> is the rows' fixed height
    /// (NaN: each row its tallest card); <paramref name="rowGap"/> the step between rows, <paramref name="colGap"/> between columns.</summary>
    public static Element Build(Element[] cells, int cols, float width, float colGap, float rowGap, float rowH = float.NaN, Func<Element>? lone = null)
    {
        cols = Math.Max(1, cols);
        int n = cells.Length, rest = Remainder(n, cols), full = n - rest;
        float cell = Basis(width, cols, colGap), last = Basis(width, Math.Max(1, rest), colGap);
        var kids = new Element[n];
        for (int i = 0; i < n; i++)
        {
            bool inLast = i >= full;
            var c = inLast && rest == 1 && cols >= 2 && lone is not null ? lone() : cells[i];
            kids[i] = new BoxEl
            {
                Key = c.Key is { Length: > 0 } k ? "fill:" + k : null,
                Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch, Height = rowH,
                // The basis decides the line, the grow the width (Basis): every card on a line grows by the same share, so a full row's cards are
                // each a column wide and the last row's share its width, at whatever width the row is laid out.
                Width = inLast ? last : cell, Grow = 1f, Shrink = 0f,
                // The wrap's gap is the column gap; a row after the first steps by the row gap instead.
                Margin = i >= cols ? new Edges4(0f, rowGap - colGap, 0f, 0f) : default,
                Children = [c],
            };
        }
        return new BoxEl { Direction = 0, Wrap = true, Gap = colGap, MinWidth = 0f, AlignItems = FlexAlign.Stretch, Children = kids };
    }

    /// <summary>A card's flex basis on a line of <paramref name="cols"/>: its share of the width <c>cols + ½</c> cards would take. The basis only
    /// breaks the lines (each card then grows to its share of the line), and this one keeps <paramref name="cols"/> cards on a line, never one
    /// more, for any width within about <c>width / (2 cols + 1)</c> of the one the cards were built at. A build that trails the width (a window
    /// drag: the grid measures its width, then rebuilds for it) breaks its lines where it was built to, instead of wrapping a full row's last
    /// card onto a line of its own (a full width basis did: the row's other cards grew into its place and the grid stood a row taller).</summary>
    static float Basis(float width, int cols, float gap) => MathF.Max(0f, (width - (cols - 1) * gap) / (cols + 0.5f));

    // ── pages of tall cards ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One page of a paged grid: its first card, how many it holds, its columns, and the list it reads (by reference: a new
    /// answer is a new page value, so the shelf re-renders it).</summary>
    public readonly record struct Page(int Start, int Count, int Cols, object Source);

    /// <summary>The rows a page of tall cards holds at most.</summary>
    public const int PageRows = 2;

    /// <summary>The pages <paramref name="count"/> tall cards take at <paramref name="fit"/> columns (rule 3), each with its columns.</summary>
    public static Page[] Pages(int count, int fit, object source)
    {
        fit = Math.Max(1, fit);
        if (count <= 0) return [];
        int rowsPerPage = Math.Min(PageRows, (count + fit - 1) / fit);
        int perPage = rowsPerPage * fit;
        int pages = (count + perPage - 1) / perPage;
        int each = (count + pages - 1) / pages;
        var list = new Page[pages];
        for (int p = 0, at = 0; p < pages; p++)
        {
            int k = Math.Min(each, count - at);
            list[p] = new Page(at, k, Columns(k, fit), source);
            at += k;
        }
        return list;
    }

    /// <summary>The height of the tallest page: per page, each row as tall as its tallest card (<paramref name="wideHeight"/> for a lone
    /// last card that takes its wide form), <paramref name="rowGap"/> between rows.</summary>
    public static float PagesHeight<T>(IReadOnlyList<T> items, Page[] pages, float rowGap, Func<T, float> height, Func<T, float> wideHeight)
    {
        float tallest = 0f;
        foreach (var pg in pages)
        {
            int rest = Remainder(pg.Count, pg.Cols);
            float total = 0f;
            for (int start = 0, r = 0; start < pg.Count; start += pg.Cols, r++)
            {
                float row = 0f;
                for (int i = start; i < Math.Min(start + pg.Cols, pg.Count); i++)
                {
                    var item = items[pg.Start + i];
                    row = MathF.Max(row, rest == 1 && pg.Cols >= 2 && i == pg.Count - 1 ? wideHeight(item) : height(item));
                }
                total += (r > 0 ? rowGap : 0f) + row;
            }
            tallest = MathF.Max(tallest, total);
        }
        return tallest;
    }

    /// <summary>The pages as ONE paged shelf of page-wide cells (snapped to a page, swipe, wheel and keys as every shelf), each page
    /// <see cref="Build"/> at its columns. Every page is laid out (a measured strip: a section holds a few), so the strip is the tallest
    /// page's height and paging never moves what is under it; <paramref name="height"/>, where the theme knows it exactly, is that height
    /// up front (NaN: measured). <paramref name="controller"/> (the chapter's, stable for its life) lets the theme draw its pager outside the shelf;
    /// <paramref name="pager"/> is what the shelf draws itself (the edge chevrons).</summary>
    public static Element Paged<T>(string key, IReadOnlyList<T> items, Page[] pages, float colGap, float rowGap, float height,
        Func<T, Element> card, Func<T, Element>? wide, ShelfController? controller, ShelfPager pager, string prevGlyph, string nextGlyph,
        TemplateParts? parts = null)
    {
        Element PageAt(Page pg, float width)
        {
            var cells = new Element[pg.Count];
            for (int i = 0; i < cells.Length; i++) cells[i] = card(items[pg.Start + i]);
            var last = items[pg.Start + pg.Count - 1];
            return new BoxEl
            {
                Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
                Children = [Build(cells, pg.Cols, width, colGap, rowGap, lone: wide is null ? null : () => wide(last))],
            };
        }
        return PagedShelf.Create<Page>(pages,
            (pg, _, w) => PageAt(pg, w),
            cardHeight: float.IsNaN(height) ? null : _ => height, measured: true,
            pager: pages.Length > 1 ? pager : ShelfPager.None,
            controller: controller,
            minCardW: PageMinW, maxCardW: PageMaxW, maxColumns: 1, gap: colGap, edgeFade: 0f, snap: ShelfSnap.Page, offPageDim: HomeModuleLayout.OffPageDim, pageMemoryKey: "grid-pages:" + key,
            prevGlyph: prevGlyph, nextGlyph: nextGlyph, parts: parts, lift: ShelfLift.None,
            keyOf: static (pg, i) => "grid-page:" + i.ToString(System.Globalization.CultureInfo.InvariantCulture))
            with { Key = "grid-pages:" + key };
    }

    /// <summary>A card shelf's band at a width, balanced over its pages (owner 2026-11-17: a shelf's page holds only its own cards): where the cards
    /// take more than one page at the band's fit, the columns are the cards spread evenly over those pages, <c>ceil(cells / ceil(cells / fit))</c>
    /// (6 at 4 are two pages of 3, not 4 and the last page slid back over two of the first), each card the width's share. One page, a strip of fixed
    /// cards, or columns that already divide evenly keep the band.</summary>
    public readonly record struct ShelfBand(int MaxColumns, float MaxCardW);

    /// <summary>The band <see cref="ShelfBand"/> describes for <paramref name="cells"/> cells (a wide lead counts its span) at <paramref name="width"/>.</summary>
    public static ShelfBand Balance(float width, int cells, float minCardW, float maxCardW, int maxColumns, float fixedCardW, float gap)
    {
        if (fixedCardW > 0f || width <= 1f || cells <= 1) return new ShelfBand(maxColumns, maxCardW);
        int fit = FluentGpu.Scene.FillRowVirtualLayout.Fit(width, minCardW, maxCardW, gap, 0, 0f, maxColumns).PerPage;
        if (cells <= fit) return new ShelfBand(maxColumns, maxCardW);
        int pages = (cells + fit - 1) / fit;
        int cols = (cells + pages - 1) / pages;
        return cols >= fit ? new ShelfBand(maxColumns, maxCardW) : new ShelfBand(cols, PageMaxW);
    }

    /// <summary>A paged card shelf at its balanced band (<see cref="Balance"/>): <paramref name="shelf"/> builds the ONE shelf with the band's
    /// column cap and card ceiling, both live props of the mounted shelf, so a width that changes the balance re-fits the same shelf (its cards,
    /// its page, the header's controller survive). Rebuilt only when the band changes.</summary>
    public static Element Balanced(string key, int cells, float minCardW, float maxCardW, int maxColumns, float fixedCardW, float gap,
        Func<ShelfBand, Element> shelf)
        => FluentGpu.Controls.Responsive.Of("balanced:" + key, w => Balance(w, cells, minCardW, maxCardW, maxColumns, fixedCardW, gap), shelf);

    /// <summary>A page cell's width band: one page across whatever the section is wide.</summary>
    const float PageMinW = 1f, PageMaxW = 1_000_000f;

    /// <summary>The live pagers by section (the zone key): the inspector's `home do=page` drives one. A chapter registers the controller it
    /// owns while it is mounted; in memory only.</summary>
    public static readonly Dictionary<string, IPager> Pagers = new(StringComparer.Ordinal);
    static readonly Dictionary<string, object> s_identity = new(StringComparer.Ordinal);

    /// <summary>Register <paramref name="controller"/> as section <paramref name="key"/>'s pager; the returned cleanup removes it (if it is still
    /// that one). A chapter calls it from an effect.</summary>
    public static Action Register(string key, ShelfController controller) => Register(key, new ShelfPagerSource(controller), controller);

    /// <summary>Register <paramref name="pager"/> as section <paramref name="key"/>'s pager (the shortcuts grid's own pages).</summary>
    public static Action Register(string key, IPager pager) => Register(key, pager, pager);

    static Action Register(string key, IPager pager, object identity)
    {
        Pagers[key] = pager;
        s_identity[key] = identity;
        return () =>
        {
            if (s_identity.TryGetValue(key, out var c) && ReferenceEquals(c, identity)) { Pagers.Remove(key); s_identity.Remove(key); }
        };
    }
}
