// ── Home/Board/Hosts/ShortcutGrid.cs: the Pinned group's shortcuts as ONE compact, paged grid (owner 2026-11-16, "one grid, edit inside it") ───
//
// The shortcut cells (the user's pins, Spotify's fill, the history tile) are one full-width board item. Inside it:
//
//   a page        at most three rows: n cells at `fit` columns take rowsPerPage = min(3, ceil(n / fit)) rows, pages = ceil(n / (rows x fit)),
//                 the cells spread evenly over the pages, each page's columns ceil(k / rows) and its last row's cells grown to fill it
//   the cells     ONE PackGrid for every page, the pages side by side in it (each page T tracks wide, T the least common multiple of every
//                 page's columns and short last row: a full row's cell spans T / columns, a last row's T / its count; a page shorter than
//                 the tallest is padded with blank rows). One grid, so a cell that changes pages (a re-plan at a new width, a drag) keeps its
//                 node and glides; each cell the theme's own tile at its small form; its reorder is the board's own (lift, siblings part, the
//                 drop commits), inside the grid
//   the pages     the grid slides (a critically damped spring on its translate; reduced motion: none) to the page the board view's
//                 ShortcutsPager shows, clipped to one page - NOT a scroller: a cell dragged sideways must move the cell, and the engine
//                 hands a sideways drag inside a sideways scroller to its pan. The Pinned group's head draws the theme's pager over the same
//                 pager; the theme's edge chevrons stand over the pages on hover. One height (the tallest page's).
//
// ACROSS PAGES: a lifted cell dragged past its page's edge lands among the next page's cells (they are laid out there, just clipped); held there a
// moment, the grid slides to that page and the drag carries on. A drop commits through the grid's reorder verb (ReorderShortcut: one document
// command, one undo entry); remove is the cell's own control and menu (the theme's tile chrome). What the theme gives is `ShortcutGridSpec`: its
// columns, its row height, its gap, its cell, when the cells lift, its edge chevrons and what stands under the pages.

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

/// <summary>One slot of the grid: a cell (its key, its entry, its place in the whole grid) or a blank that pads a short page, its page, its span of
/// the page's tracks.</summary>
public readonly record struct ShortcutCellItem(string Key, BoardEntry? Entry, int Index, int Page, byte Span);

/// <summary>The grid's plan at a width: the cells, the pages (start, count, columns, tracks), the row height, the tallest page's rows and height,
/// and the tracks every page is cut into.</summary>
public sealed record ShortcutGridPlan(IReadOnlyList<BoardEntry> Cells, ShortcutPage[] Pages, float Unit, float Gap, float PageHeight, int Rows = 0,
    int Tracks = 1)
{
    /// <summary>Every slot, row by row across the pages (the PackGrid fills its rows in order): page 0's first row, page 1's first row, ..., then the
    /// second rows; a page with fewer rows than the tallest is padded with one blank a page wide per missing row.</summary>
    public IReadOnlyList<ShortcutCellItem> Items()
    {
        var items = new List<ShortcutCellItem>(Cells.Count + Pages.Length * Rows);
        for (int r = 0; r < Rows; r++)
            for (int p = 0; p < Pages.Length; p++)
            {
                var pg = Pages[p];
                int rows = GridFill.Rows(pg.Count, pg.Cols);
                if (r >= rows)
                {
                    items.Add(new ShortcutCellItem("home:shortcut-blank:" + p.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" +
                                                   r.ToString(System.Globalization.CultureInfo.InvariantCulture), null, -1, p, (byte)Tracks));
                    continue;
                }
                int rest = GridFill.Remainder(pg.Count, pg.Cols), full = pg.Count - rest;
                int start = r * pg.Cols, end = Math.Min(pg.Count, start + pg.Cols);
                for (int i = start; i < end; i++)
                {
                    var e = Cells[pg.Start + i];
                    int across = i >= full ? rest : pg.Cols;
                    // Keyed by what the cell IS (the pin), not by its entry: a fill cell that becomes the user's pin on a reorder keeps its node.
                    string key = e.Raw is PinSource pin ? "home:shortcut:pin:" + pin.PinId : "home:shortcut:" + e.Id;
                    items.Add(new ShortcutCellItem(key, e, pg.Start + i, p, (byte)(Tracks / Math.Max(1, across))));
                }
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

    static int Lcm(int a, int b)
    {
        int x = a, y = b;
        while (y != 0) (x, y) = (y, x % y);
        return a / x * b;
    }

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
        int tracks = 1;
        foreach (var pg in pages) tracks = Lcm(tracks, pg.Tracks);
        return new ShortcutGridPlan(cells, pages, unit, spec.Gap, tallest * unit + (tallest - 1) * spec.Gap, tallest, tracks);
    }

    /// <summary>The grid item's height at <paramref name="width"/>: the tallest page, and what stands under the strip while it pages.</summary>
    public static float Extent(BoardModel m, ShortcutGridSpec spec, float width)
    {
        var plan = Plan(HomeShortcuts.Cells(m), spec, width);
        return plan.PageHeight + (plan.Pages.Length > 1 && spec.Below is not null ? spec.Gap + spec.BelowH : 0f);
    }
}

/// <summary>The grid: its width (measured; the board's estimate on the first frame), the plan and its slots (memos of the board's model and the width),
/// the clipped pages.</summary>
public sealed class ShortcutGridView : Component
{
    readonly Signal<float> _width = new(0f);
    readonly Signal<bool> _hover = new(false);
    readonly Action<RectF> _onBounds;
    readonly Action<Point2> _enter;
    readonly Action _exit;
    ShortcutGridProps? _p;

    /// <summary>The plan and the grid's slots.</summary>
    internal Memo<ShortcutGridPlan>? Plan { get; private set; }
    internal Memo<IReadOnlyList<ShortcutCellItem>>? Items { get; private set; }

    internal HomeBoardProps Board => _p!.Board;
    internal ShortcutGridSpec Spec => _p!.Spec;

    /// <summary>The mounted track (it registers itself on render): the clip box forwards the swipe and the wheel to it.</summary>
    internal ShortcutTrack? Track { get; set; }

    readonly Action<Point2> _panDown, _panMove;
    readonly Action _panCommit, _panCancel;
    readonly Action<WheelEventArgs> _onWheel;
    internal float Width => _width.Peek() > 0f ? _width.Peek() : _p?.FirstWidth ?? 0f;

    public ShortcutGridView()
    {
        _onBounds = r => { if (r.W > 0f && MathF.Abs(r.W - _width.Peek()) > 0.25f) _width.Value = r.W; };
        _enter = _ => { if (!_hover.Peek()) _hover.Value = true; };
        _exit = () => { if (_hover.Peek()) _hover.Value = false; };
        _panDown = lp => Track?.PanDown(lp);
        _panMove = lp => Track?.PanMove(lp);
        _panCommit = () => Track?.PanCommit();
        _panCancel = () => Track?.PanCancel();
        _onWheel = e => Track?.OnWheel(e);
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
        var plans = Plan;
        Items = UseComputed(() => plans.Value.Items());
        var pager = p.Board.View.ShortcutsPager;
        int count = plan.Pages.Length;
        UseEffect(() => pager.Publish(count), DepKey.From(count));
        // The inspector's `home do=page key=shortcuts` turns these pages.
        UseEffect(() => GridFill.Register("home:shortcuts", pager));
        var spec = p.Spec;
        var track = Embed.Comp(new TrackProps(this, Math.Max(1, count)), static () => new ShortcutTrack()) with { Key = "home:shortcuts-track" };
        var layers = new List<Element>(2)
        {
            new BoxEl
            {
                Direction = 0, MinWidth = 0f, Height = plan.PageHeight, ClipToBounds = true,
                // FlipView's pan on the still frame: a sideways drag that no cell lifts follows the pointer and lands on the nearest page (a touch
                // that starts on a cell reaches it through the gesture arena; a cross-axis drag yields to the page's scroll). A horizontal wheel
                // turns a page.
                OnPointerDown = count > 1 ? _panDown : null,
                OnDrag = count > 1 ? _panMove : null,
                DragYieldsToPan = count > 1,
                OnClick = count > 1 ? _panCommit : null,
                OnDragCanceled = count > 1 ? _panCancel : null,
                OnPointerWheel = count > 1 ? _onWheel : null,
                Children = [track],
            },
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

    internal sealed record TrackProps(ShortcutGridView Grid, int Count);
}

/// <summary>The pages: ONE PackGrid as wide as every page side by side (a page the grid's width, the gap between them), slid to the page shown (its
/// own root's translate on a critically damped spring; none under reduced motion). The reorder is the board's own.</summary>
public sealed class ShortcutTrack : Component
{
    static readonly SpringParams s_slide = SpringParams.FromResponse(ShortcutGrid.SlideMs / 1000f, 1f);

    readonly PackGridController _controller = new();
    ShortcutGridView.TrackProps? _p;
    PackReorder<ShortcutCellItem>? _reorder;
    PackGridOptions<ShortcutCellItem>? _options;
    Func<ShortcutCellItem, PackCell, Element>? _template;
    TimerHandle _turn;
    readonly Action _onTurn;
    int _turnTo = -1;   // the page a lifted cell is held over (not the one shown), -1 none
    // The swipe (FlipView's pan: follow the pointer, release to the nearest page) and the horizontal wheel.
    readonly Action<NodeHandle> _onRealized;
    readonly Keyframe[] _panKeys = new Keyframe[2];
    readonly Signal<int> _panGen = new(0);   // a released swipe re-seeds the slide even when it stays on its page
    NodeHandle _node;
    float _panStart, _panBase, _panOffset, _wheel;
    bool _panning, _panArmed;
    long _wheelAt;

    /// <summary>The swipe's dead zone (the engine's drag box) and the share of a page a release must pass to turn it.</summary>
    const float PanSlopPx = 4f, PanTurnShare = 0.2f;

    /// <summary>A horizontal wheel turns a page per this much travel (one detent), and not again within the slide.</summary>
    const float WheelPage = 120f, WheelQuietMs = 360f;

    /// <summary>How long a lifted cell waits over another page before the grid slides there (the board's own dwell).</summary>
    public const float TurnDwellMs = 600f;

    public ShortcutTrack()
    {
        _onTurn = Turn;
        _onRealized = h => _node = h;
    }

    public override Element Render()
    {
        var p = UseProps<ShortcutGridView.TrackProps>();
        _p = p;
        var grid = p.Grid;
        var pager = grid.Board.View.ShortcutsPager;
        _ = pager.Page.Value;   // subscribe: a turn re-renders the track
        int page = pager.FirstPage(p.Count);
        float width = grid.Width, gap = grid.Spec.Gap;
        float step = width + gap;
        float to = -page * step;
        int gen = _panGen.Value;
        // The grid mounts on its page (a remembered one too): no slide in. After that the slide is a critically damped spring from wherever the
        // grid is (a turn mid-slide or a released swipe carries on); reduced motion lands at once.
        UseTransition(AnimChannel.TranslateX, to, to, 0f, Easing.Linear, DepKey.Empty);
        if (Motion.ReducedMotion) UseTransition(AnimChannel.TranslateX, to, to, 0f, Easing.Linear, DepKey.From(page, HashCode.Combine((int)step, gen)));
        else UseSpring(AnimChannel.TranslateX, to, s_slide, DepKey.From(page, HashCode.Combine((int)step, gen)));
        _turn = UseTimeout(_onTurn, TurnDwellMs);

        // Each cell dims while it stands off the page shown (the page sliding off at 0.8, the one sliding in easing to 1).
        _template ??= (item, cell) => item.Entry is { } e
            ? Embed.Comp(new ShortcutDim.Props(_p!.Grid.Board.View.ShortcutsPager, item.Page, _p.Grid.Spec.Cell(e, item.Index, cell)),
                static () => new ShortcutDim())
            : new BoxEl();
        _reorder ??= new PackReorder<ShortcutCellItem>("home.shortcut")
        {
            // A cell showing Recently played is not the shortcut it stands for: it lifts once the grid shows the shortcuts again. A blank never lifts.
            CanLift = it => it.Entry is { } e && _p!.Grid.Board.View.ShortcutsFace.Peek() == HomeShortcuts.ShortcutsFace && !_p.Grid.Board.ReadOnly
                            && (_p.Grid.Spec.CanLift?.Invoke(e) ?? true),
            Enabled = () => _p!.Grid.Spec.CanReorder(),
            OnCommit = m => Commit(m),
            OnCancel = _ => Hold(-1),
            AnnounceText = a => Announce(a),
        };
        _options ??= new PackGridOptions<ShortcutCellItem>
        {
            KeyOf = static it => it.Key,
            EntryOf = static it => new PackEntry(PackEntryKind.Tile, Cols: it.Span, Rows: 1),
            Metrics = () =>
            {
                var plan = _p!.Grid.Plan!.Value;
                return new PackMetrics(Math.Max(1, plan.Pages.Length) * plan.Tracks, plan.Unit, plan.Gap, 0f);
            },
            Controller = _controller,
            // The grid's scroller is the board's (Home's page): it realizes against the page.
            Scroll = grid.Board.Scroll,
            Reorder = _reorder,
            Key = "home:shortcuts-grid",
        };
        grid.Track = this;   // the view's still clip box carries the swipe and the wheel (a handler on this moving root reads its own motion)
        return new BoxEl
        {
            Direction = 0, Shrink = 0f, AlignItems = FlexAlign.Stretch,
            Width = p.Count * width + (p.Count - 1) * gap,
            OnRealized = _onRealized,
            Children = [new BoxEl { Direction = 1, Grow = 1f, AlignItems = FlexAlign.Stretch, Children = [PackGrid.Create(grid.Items!, _template, _options)] }],
        };
    }

    internal void PanDown(Point2 lp)
    {
        _panStart = lp.X;
        _panBase = -_p!.Grid.Board.View.ShortcutsPager.Page.Peek() * (_p.Grid.Width + _p.Grid.Spec.Gap);
        if (Context.Anim is { } anim && !_node.IsNull && anim.TryGetTrackValue(_node, AnimChannel.TranslateX, out float live)) _panBase = live;
        _panOffset = _panBase;
        _panning = false;
        _panArmed = true;
    }

    internal void PanMove(Point2 lp)
    {
        if (_p is not { } p || _node.IsNull || _reorder is { IsLifted: true }) return;
        // A touch swipe that started on a cell reaches the grid through the gesture arena: its first move is the grid's down.
        if (!_panArmed) { PanDown(lp); return; }
        float delta = lp.X - _panStart;
        if (!_panning && MathF.Abs(delta) < PanSlopPx) return;
        _panning = true;
        float step = p.Grid.Width + p.Grid.Spec.Gap;
        float off = Math.Clamp(_panBase + delta, _panBase - step, _panBase + step);
        off = Math.Clamp(off, -(p.Count - 1) * step - step * 0.15f, step * 0.15f);   // a little give past either end
        _panKeys[0] = new Keyframe(0f, off, Easing.Linear);
        _panKeys[1] = new Keyframe(1f, off, Easing.Linear);
        Context.Anim?.Keyframes(_node, AnimChannel.TranslateX, _panKeys, 1f);
        _panOffset = off;
    }

    internal void PanCommit()
    {
        _panArmed = false;
        if (!_panning || _p is not { } p) return;
        _panning = false;
        var pager = p.Grid.Board.View.ShortcutsPager;
        float step = p.Grid.Width + p.Grid.Spec.Gap;
        float moved = _panOffset - _panBase;
        int page = pager.Page.Peek();
        if (moved < -step * PanTurnShare) pager.Next();
        else if (moved > step * PanTurnShare) pager.Prev();
        if (pager.Page.Peek() == page) _panGen.Value = _panGen.Peek() + 1;   // back to its page: the spring re-seeds from where it was let go
    }

    internal void PanCancel()
    {
        _panArmed = false;
        if (!_panning) return;
        _panning = false;
        _panGen.Value = _panGen.Peek() + 1;
    }

    internal void OnWheel(WheelEventArgs e)
    {
        if (MathF.Abs(e.DeltaX) <= MathF.Abs(e.Delta) || _p is not { } p) return;   // a vertical wheel scrolls the page
        e.Handled = true;
        long now = Environment.TickCount64;
        if (now - _wheelAt < WheelQuietMs) return;   // one page per flick: the rest of the gesture lands inside the slide
        _wheel += e.DeltaX;
        if (MathF.Abs(_wheel) < WheelPage) return;
        var pager = p.Grid.Board.View.ShortcutsPager;
        if (_wheel > 0f) pager.Next(); else pager.Prev();
        _wheel = 0f;
        _wheelAt = now;
    }

    /// <summary>The reorder's milestones: where the lifted cell is (the board's own sentence), and whether it is held over another page.</summary>
    string? Announce(PackAnnounce a)
    {
        var items = _p!.Grid.Items?.Peek() ?? [];
        int shown = _p.Grid.Board.View.ShortcutsPager.Page.Peek();
        if (a.Kind == PackAnnounceKind.Move && (uint)(a.Position - 1) < (uint)items.Count)
        {
            int over = items[a.Position - 1].Page;
            Hold(over != shown ? over : -1);
        }
        else Hold(-1);
        string id = "";
        foreach (var it in items) if (it.Key == a.Key && it.Entry is { } e) { id = e.Id; break; }
        return a.Kind is PackAnnounceKind.Move or PackAnnounceKind.Drop && id.Length > 0 ? _p.Grid.Board.Verbs.Where(id) : null;
    }

    /// <summary>The lifted cell is held over a page that is not shown (or not any more): the dwell starts over, or stops.</summary>
    void Hold(int page)
    {
        if (page == _turnTo) return;
        _turnTo = page;
        if (page < 0) _turn.Cancel();
        else _turn.RestartIn(TurnDwellMs);
    }

    /// <summary>The dwell ran out over another page: the grid slides there and the drag carries on.</summary>
    void Turn()
    {
        int page = _turnTo;
        if (page < 0 || _p is not { } p || _reorder is not { IsLifted: true }) return;
        p.Grid.Board.View.ShortcutsPager.GoTo(page);
    }

    /// <summary>A drop: the grid's reorder verb, the cell to the place the drop's slot holds in the whole grid (one command, one undo entry); a drop on a
    /// short page's blank lands at that page's end.</summary>
    void Commit(PackMove m)
    {
        Hold(-1);
        var grid = _p!.Grid;
        var items = grid.Items?.Peek() ?? [];
        if ((uint)m.From >= (uint)items.Count || (uint)m.To >= (uint)items.Count || m.From == m.To || items[m.From].Entry is not { } moving) return;
        var target = items[m.To];
        int to = target.Index;
        if (target.Entry is null && grid.Plan?.Peek() is { } plan && (uint)target.Page < (uint)plan.Pages.Length)
        {
            var pg = plan.Pages[target.Page];
            to = Math.Min(pg.Start + pg.Count - 1, plan.Cells.Count - 1);
        }
        if (to >= 0) grid.Board.Verbs.ReorderShortcut(moving.Id, to);
    }
}

/// <summary>A shortcut cell that dims while it stands off the page shown: the page sliding off eases to 0.8, the page sliding in to 1 (the slide's
/// 420 ms, decelerating; reduced motion: at once).</summary>
public sealed class ShortcutDim : Component
{
    internal sealed record Props(IPager Pager, int Page, Element Cell);

    public override Element Render()
    {
        var p = UseProps<Props>();
        bool on = p.Pager.Page.Value == p.Page;
        float dim = 1f - HomeModuleLayout.OffPageDim;
        UseTransition(AnimChannel.Opacity, on ? dim : 1f, on ? 1f : dim, Motion.ReducedMotion ? 0f : ShortcutGrid.SlideMs, Easing.FluentDecelerate,
            DepKey.From(on ? 1 : 0));
        return new BoxEl { Direction = 1, Grow = 1f, MinWidth = 0f, AlignItems = FlexAlign.Stretch, Children = [p.Cell] };
    }
}
