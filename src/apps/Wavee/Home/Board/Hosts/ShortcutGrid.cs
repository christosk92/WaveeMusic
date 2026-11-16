// ── Home/Board/Hosts/ShortcutGrid.cs: the Pinned group's shortcuts as ONE compact, paged grid (owner 2026-11-16, "one grid, edit inside it") ───
//
// The shortcut cells (the user's pins, Spotify's fill, the history tile) are one full-width board item. Inside it:
//
//   the pages     a track of page-wide boxes, clipped, that slides to the page the board view's ShortcutsPager shows (a 420 ms decelerating
//                 translate; reduced motion: none) - NOT a scroller: a cell dragged sideways inside a page must move the cell, and the engine
//                 hands a sideways drag inside a sideways scroller to its pan. The Pinned group's head draws the theme's pager over the same
//                 pager; the theme's edge chevrons stand over the track on hover. One height (the tallest page's).
//   a page        at most three rows: n cells at `fit` columns take rowsPerPage = min(3, ceil(n / fit)) rows, pages = ceil(n / (rows x fit)),
//                 the cells spread evenly over the pages, each page's columns ceil(k / rows) and its last row's cells grown to fill it
//   the cells     a PackGrid per page (its tracks the least common multiple of its columns and its short last row's count: a full row's cell
//                 spans tracks / columns, a last row's tracks / its count), each cell the
//                 theme's own tile at its small form; its reorder is the board's own (lift, siblings part, the drop commits), inside the grid
//
// ACROSS PAGES: a lifted cell held at its page's first or last place for a moment carries over to the page before or after (it lands at that
// page's near end, the strip slides there); the drop there is an ordinary drop.
// A drop commits through the grid's reorder verb (ReorderShortcut: one document command, one undo entry); remove is the cell's own control and menu
// (the theme's tile chrome). What the theme gives is `ShortcutGridSpec`: its columns, its row height, its gap, its cell, when the cells lift,
// what the strip draws itself and what stands under it.

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;

namespace Wavee;

/// <summary>What a theme's shortcuts grid is: its columns at a width, its row height at a column's width, its gap, its cell, when the cells lift (its
/// edit mode) and which one may (Zune: the selected), its edge chevrons (previous: true; drawn over the pages, shown on hover) and what stands under
/// the pages (WinUI's pips), and how tall that is.</summary>
public sealed record ShortcutGridSpec(
    Func<float, int> Columns,
    Func<float, float> Unit,
    float Gap,
    Func<BoardEntry, int, PackCell, Element> Cell,
    Func<bool> CanReorder,   // read inside each page grid's render: a signal it reads (the edit mode) re-wires the lift
    Func<bool, IPager, Element>? Edge = null,
    Func<IPager, Element>? Below = null,
    float BelowH = 0f,
    Func<BoardEntry, bool>? CanLift = null);

/// <summary>One cell as a page packs it: its key, its entry, its place in the whole grid and its span of the page's twelve tracks.</summary>
public readonly record struct ShortcutCellItem(string Key, BoardEntry Entry, int Index, byte Span);

/// <summary>The grid's plan at a width: the cells, the pages (start, count, columns), the row height and the height of the tallest page.</summary>
public sealed record ShortcutGridPlan(IReadOnlyList<BoardEntry> Cells, ShortcutPage[] Pages, float Unit, float Gap, float PageHeight)
{
    /// <summary>A page's cells as its PackGrid packs them (empty past the last page).</summary>
    public IReadOnlyList<ShortcutCellItem> Items(int page)
    {
        if ((uint)page >= (uint)Pages.Length) return [];
        var pg = Pages[page];
        int rest = GridFill.Remainder(pg.Count, pg.Cols), full = pg.Count - rest;
        var items = new ShortcutCellItem[pg.Count];
        for (int i = 0; i < pg.Count; i++)
        {
            var e = Cells[pg.Start + i];
            int across = i >= full ? rest : pg.Cols;
            // Keyed by what the cell IS (the pin), not by its entry: a fill cell that becomes the user's pin on a reorder keeps its node and glides.
            string key = e.Raw is PinSource pin ? "home:shortcut:pin:" + pin.PinId : "home:shortcut:" + e.Id;
            items[i] = new ShortcutCellItem(key, e, pg.Start + i, (byte)(pg.Tracks / Math.Max(1, across)));
        }
        return items;
    }
}

/// <summary>A page: its first cell, how many it holds, its columns, and its tracks (every row's cells span a whole number of them).</summary>
public readonly record struct ShortcutPage(int Start, int Count, int Cols, int Tracks);

/// <summary>The shortcuts grid's props: the board, the theme's spec, the board's first-frame width.</summary>
public sealed record ShortcutGridProps(HomeBoardProps Board, ShortcutGridSpec Spec, float FirstWidth);

public static class ShortcutGrid
{
    /// <summary>The rows a page holds at most.</summary>
    public const int MaxRows = 3;

    /// <summary>A page slide's response (a critically damped spring: it decelerates into the page).</summary>
    public const float SlideMs = 420f;

    /// <summary>A page's tracks: the least common multiple of its columns and its last row's count, so every cell spans a whole number of them.</summary>
    public static int TracksOf(int cols, int count)
    {
        int rest = GridFill.Remainder(count, cols);
        if (rest == 0) return Math.Max(1, cols);
        int a = cols, b = rest;
        while (b != 0) (a, b) = (b, a % b);
        return cols / a * rest;
    }

    /// <summary>The grid as one board item (the theme's board packs it as a full row at <see cref="Extent"/>).</summary>
    public static Element Create(HomeBoardProps board, ShortcutGridSpec spec, float firstWidth)
        => Embed.Comp(new ShortcutGridProps(board, spec, firstWidth), static () => new ShortcutGridView()) with { Key = "home:shortcuts" };

    /// <summary>The plan of <paramref name="cells"/> at <paramref name="width"/>.</summary>
    public static ShortcutGridPlan Plan(IReadOnlyList<BoardEntry> cells, ShortcutGridSpec spec, float width)
    {
        int n = cells.Count;
        int fit = Math.Max(1, spec.Columns(width));
        if (n == 0) return new ShortcutGridPlan(cells, [], spec.Unit(width), spec.Gap, 0f);
        int rowsPerPage = Math.Min(MaxRows, (n + fit - 1) / fit);
        int perPage = rowsPerPage * fit;
        int pageCount = (n + perPage - 1) / perPage;
        int each = (n + pageCount - 1) / pageCount;
        var pages = new ShortcutPage[pageCount];
        int tallest = 0;
        for (int p = 0, at = 0; p < pageCount; p++)
        {
            int k = Math.Min(each, n - at);
            int cols = GridFill.Columns(k, fit);
            pages[p] = new ShortcutPage(at, k, cols, ShortcutGrid.TracksOf(cols, k));
            tallest = Math.Max(tallest, GridFill.Rows(k, cols));
            at += k;
        }
        float unit = spec.Unit(GridFill.CellWidth(width, fit, spec.Gap));
        return new ShortcutGridPlan(cells, pages, unit, spec.Gap, tallest * unit + (tallest - 1) * spec.Gap);
    }

    /// <summary>The grid item's height at <paramref name="width"/>: the tallest page, and what stands under the strip while it pages.</summary>
    public static float Extent(BoardModel m, ShortcutGridSpec spec, float width)
    {
        var plan = Plan(HomeShortcuts.Cells(m), spec, width);
        return plan.PageHeight + (plan.Pages.Length > 1 && spec.Below is not null ? spec.Gap + spec.BelowH : 0f);
    }
}

/// <summary>The grid: its width (measured; the board's estimate on the first frame), the plan (a memo of the board's model and the width), the
/// sliding track of pages.</summary>
public sealed class ShortcutGridView : Component
{
    readonly Signal<float> _width = new(0f);
    readonly Signal<bool> _hover = new(false);
    readonly Action<RectF> _onBounds;
    readonly Action<Point2> _enter;
    readonly Action _exit;
    ShortcutGridProps? _p;

    /// <summary>The plan the pages read (each page its own slice).</summary>
    internal Memo<ShortcutGridPlan>? Plan { get; private set; }

    internal HomeBoardProps Board => _p!.Board;
    internal ShortcutGridSpec Spec => _p!.Spec;

    public ShortcutGridView()
    {
        _onBounds = r => { if (r.W > 0f && MathF.Abs(r.W - _width.Peek()) > 0.25f) _width.Value = r.W; };
        _enter = _ => { if (!_hover.Peek()) _hover.Value = true; };
        _exit = () => { if (_hover.Peek()) _hover.Value = false; };
    }

    public override Element Render()
    {
        var p = UseProps<ShortcutGridProps>();
        _p = p;
        Plan = UseComputed(() =>
        {
            float w = _width.Value > 0f ? _width.Value : _p!.FirstWidth;
            return ShortcutGrid.Plan(HomeShortcuts.Cells(_p!.Board.Model.Value), _p.Spec, w);
        });
        var plan = Plan.Value;
        var pager = p.Board.View.ShortcutsPager;
        int count = plan.Pages.Length;
        UseEffect(() => pager.Publish(count), DepKey.From(count));
        // The inspector's `home do=page key=shortcuts` turns these pages.
        UseEffect(() => GridFill.Register("home:shortcuts", pager));
        var spec = p.Spec;
        float width = _width.Peek() > 0f ? _width.Peek() : p.FirstWidth;
        var track = Embed.Comp(new TrackProps(this, count, width, spec.Gap, plan.PageHeight), static () => new ShortcutTrack()) with { Key = "home:shortcuts-track" };
        var layers = new List<Element>(3)
        {
            new BoxEl { Direction = 0, MinWidth = 0f, Height = plan.PageHeight, ClipToBounds = true, Children = [track] },
        };
        if (count > 1 && spec.Edge is { } edge)
        {
            var hover = _hover;
            layers.Add(new BoxEl
            {
                Direction = 0, Justify = FlexJustify.SpaceBetween, AlignItems = FlexAlign.Center, MinWidth = 0f, HitTestVisible = false,
                Children =
                [
                    new BoxEl { HitTestVisible = true, Visible = Prop.Of(() => hover.Value && pager.CanPrev.Value), Children = [edge(true, pager)] },
                    new BoxEl { HitTestVisible = true, Visible = Prop.Of(() => hover.Value && pager.CanNext.Value), Children = [edge(false, pager)] },
                ],
            });
        }
        var kids = new List<Element>(2)
        {
            new BoxEl { Direction = 1, MinWidth = 0f, ZStack = true, OnHoverMove = _enter, OnPointerExit = _exit, Children = layers.ToArray() },
        };
        if (count > 1 && spec.Below is { } below)
            kids.Add(new BoxEl { Direction = 0, Height = spec.BelowH, MinWidth = 0f, Justify = FlexJustify.Center, AlignItems = FlexAlign.Center, Children = [below(pager)] });
        return new BoxEl
        {
            Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch, Gap = spec.Gap, OnBoundsChanged = _onBounds, Children = kids.ToArray(),
        };
    }

    internal sealed record PageProps(ShortcutGridView Grid, int Index);

    internal sealed record TrackProps(ShortcutGridView Grid, int Count, float Width, float Gap, float Height);
}

/// <summary>The pages side by side, each the grid's width; the track slides to the page shown (its own root's translate, eased; none under reduced
/// motion). A page is keyed by its place, so a page's cells keep their grid across a re-plan.</summary>
public sealed class ShortcutTrack : Component
{
    static readonly SpringParams s_slide = SpringParams.FromResponse(ShortcutGrid.SlideMs / 1000f, 1f);

    public override Element Render()
    {
        var p = UseProps<ShortcutGridView.TrackProps>();
        var grid = p.Grid;
        int page = Math.Clamp(grid.Board.View.ShortcutsPager.Page.Value, 0, Math.Max(0, p.Count - 1));
        float step = p.Width + p.Gap;
        float to = -page * step;
        // The slide is a critically damped spring from wherever the track is (a turn mid-slide carries on); reduced motion lands at once.
        if (Motion.ReducedMotion) UseTransition(AnimChannel.TranslateX, to, to, 0f, Easing.Linear, DepKey.From(page, (int)step));
        else UseSpring(AnimChannel.TranslateX, to, s_slide, DepKey.From(page, (int)step));
        var pages = new Element[Math.Max(1, p.Count)];
        for (int i = 0; i < pages.Length; i++)
            pages[i] = new BoxEl
            {
                Key = "shortcuts-page:" + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Direction = 1, Width = p.Width, Height = p.Height, Shrink = 0f, AlignItems = FlexAlign.Stretch,
                Children = [Embed.Comp(new ShortcutGridView.PageProps(grid, i), static () => new ShortcutPageView())],
            };
        return new BoxEl { Direction = 0, Gap = p.Gap, Shrink = 0f, Children = pages };
    }
}

/// <summary>One page: its cells in a PackGrid of twelve tracks, reordered inside it with the board's own lift.</summary>
public sealed class ShortcutPageView : Component
{
    readonly PackGridController _controller = new();
    ShortcutGridView.PageProps? _p;
    PackReorder<ShortcutCellItem>? _reorder;
    PackGridOptions<ShortcutCellItem>? _options;
    Func<ShortcutCellItem, PackCell, Element>? _template;
    Memo<IReadOnlyList<ShortcutCellItem>>? _items;
    TimerHandle _turn;
    readonly Action _onTurn;
    int _edge;   // the edge a lifted cell is held at: -1 the page's first place, 1 its last, 0 neither

    /// <summary>How long a lifted cell waits at its page's edge before it carries over (the board's own dwell).</summary>
    public const float TurnDwellMs = 600f;

    public ShortcutPageView() => _onTurn = Turn;

    public override Element Render()
    {
        var p = UseProps<ShortcutGridView.PageProps>();
        _p = p;
        int index = p.Index;
        var grid = p.Grid;
        _items = UseComputed(() => grid.Plan?.Value.Items(index) ?? []);
        _turn = UseTimeout(_onTurn, TurnDwellMs);
        _template ??= (item, cell) => _p!.Grid.Spec.Cell(item.Entry, item.Index, cell);
        _reorder ??= new PackReorder<ShortcutCellItem>("home.shortcut")
        {
            // A cell showing Recently played is not the shortcut it stands for: it lifts once the grid shows the shortcuts again.
            CanLift = it => _p!.Grid.Board.View.ShortcutsFace.Peek() == HomeShortcuts.ShortcutsFace && !_p.Grid.Board.ReadOnly
                            && (_p.Grid.Spec.CanLift?.Invoke(it.Entry) ?? true),
            Enabled = () => _p!.Grid.Spec.CanReorder(),
            OnCommit = m => Commit(m),
            OnCancel = _ => Hold(0),
            AnnounceText = a => Announce(a),
        };
        _options ??= new PackGridOptions<ShortcutCellItem>
        {
            KeyOf = static it => it.Key,
            EntryOf = static it => new PackEntry(PackEntryKind.Tile, Cols: it.Span, Rows: 1),
            Metrics = () =>
            {
                var plan = _p!.Grid.Plan!.Value;
                int tracks = (uint)_p.Index < (uint)plan.Pages.Length ? plan.Pages[_p.Index].Tracks : 1;
                return new PackMetrics(tracks, plan.Unit, plan.Gap, 0f);
            },
            Controller = _controller,
            // The page's scroller is the board's (Home's page), never the paged strip around it: the grid realizes against the page, and a lifted
            // cell dragged sideways moves the cell, it does not scroll the strip.
            Scroll = _p!.Grid.Board.Scroll,
            Reorder = _reorder,
            Key = "home:shortcuts-page",
        };
        return PackGrid.Create(_items, _template, _options);
    }

    /// <summary>The reorder's milestones: where the lifted cell is (the board's own sentence), and whether it is held at the page's first or last place.</summary>
    string? Announce(PackAnnounce a)
    {
        var items = _items?.Peek() ?? [];
        if (a.Kind == PackAnnounceKind.Move) Hold(a.Position <= 1 ? -1 : a.Position >= items.Count ? 1 : 0);
        else Hold(0);
        string id = "";
        foreach (var it in items) if (it.Key == a.Key) { id = it.Entry.Id; break; }
        return a.Kind is PackAnnounceKind.Move or PackAnnounceKind.Drop && id.Length > 0 ? _p!.Grid.Board.Verbs.Where(id) : null;
    }

    /// <summary>The lifted cell is held at an edge (or left it): the dwell starts over, or stops.</summary>
    void Hold(int edge)
    {
        if (edge == _edge) return;
        _edge = edge;
        if (edge == 0) _turn.Cancel();
        else _turn.RestartIn(TurnDwellMs);
    }

    /// <summary>The dwell ran out at an edge with a page beyond it: the lift ends there and the cell carries over, to the next page's first place
    /// (or the previous page's last), and the strip slides to it.</summary>
    void Turn()
    {
        int edge = _edge;
        _edge = 0;
        if (edge == 0 || _p is not { } p || _reorder is not { IsLifted: true } reorder || p.Grid.Plan?.Peek() is not { } plan) return;
        int page = p.Index + edge;
        if ((uint)page >= (uint)plan.Pages.Length) return;
        string? key = reorder.LiftedKey;
        string id = "";
        foreach (var it in _items?.Peek() ?? []) if (it.Key == key) { id = it.Entry.Id; break; }
        if (id.Length == 0) return;
        var target = plan.Pages[page];
        int to = edge > 0 ? target.Start : target.Start + target.Count - 1;
        reorder.Cancel();
        p.Grid.Board.Verbs.ReorderShortcut(id, to);
        var pager = p.Grid.Board.View.ShortcutsPager;
        if (edge > 0) pager.Next(); else pager.Prev();
    }

    /// <summary>A drop inside the page: the grid's reorder verb, the cell to the place the drop's slot holds in the whole grid (one command, one undo
    /// entry).</summary>
    void Commit(PackMove m)
    {
        var items = _items?.Peek() ?? [];
        if ((uint)m.From >= (uint)items.Count || (uint)m.To >= (uint)items.Count || m.From == m.To) return;
        _p!.Grid.Board.Verbs.ReorderShortcut(items[m.From].Entry.Id, items[m.To].Index);
    }
}
