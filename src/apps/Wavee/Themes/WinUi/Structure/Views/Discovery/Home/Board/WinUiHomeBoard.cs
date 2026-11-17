// ── Themes/WinUi/Structure/Views/Discovery/Home/Board/WinUiHomeBoard.cs — WinUI's `Home.Board` (pinned-home-tech.md 1.7; spec 8.1) ───────
//
// Windows 11 Start and the Widgets board: NO edit mode. Every card always has its ⋯ (a reserved corner in every form) and always drags; "Add widgets"
// in the page's head row opens a band under the header (`WinUiHomeAddBand`); a card opens its own settings in place (`WinUiHomeTileOptions`).
//
//   Home.Board
//   └─ column  (keys: Ctrl+Z, "/", Escape; the board's width for its column count)
//      ├─ the add band   a FlowReveal disclosure keyed `home:add`: its height animates open and shut, the board slides under it
//      └─ PackGrid       keyed `home:board`; group heads (a quiet 14 semibold label), the empty Pinned row, one wrapper `home:tile:<id>` per entry
//
// THE BOX IS THE PACK'S: a tile's rect is f(size class, column count, the group's fill) and nothing a tile loads. A FULL row's height is its body's (the
// chapter's own reserved height at the fitted width): it packs at the rules' estimate, then at the body's measured height, and the wrapper follows by the
// grid's 250 ms re-flow, never by a snap. The open tile packs at its options' height.

using System.Collections.Generic;
using System.Collections.Immutable;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.HomeUi;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.WinUi;

/// <summary>What the WinUI grid packs: a board entry (the shortcuts grid one of them), or the empty Pinned row the theme adds after an empty group head.
/// <see cref="Switch"/> says the Pinned group carries the history tile, so its head offers the Shortcuts / Recently played switch and the grid's cells
/// cross-fade (<see cref="HomeShortcuts"/>).</summary>
internal sealed record WinUiBoardItem(string Key, BoardEntry Entry, bool EmptyRow, bool Switch = false);

/// <summary>WinUI's pinned Home board.</summary>
internal static class WinUiHomeBoard
{
    /// <summary>The registered template of <see cref="ViewKeys.HomeBoard"/>.</summary>
    public static Element Template(HomeBoardProps props, in ViewContext ctx)
    {
        var facts = ctx.Tier();
        // The board's first pack uses the region's own estimate of its width (the Recently played grid seeds the same way): measured one frame
        // late, a zero width packed one column and the whole board re-packed and re-laid on the next frame.
        float firstWidth = WinUiRegion.EstimateWidth(facts, ctx.Hooks.UseContextSignal(Viewport.Size).Peek().Width);
        return Embed.Comp(new BoardProps(props, WinUiHomeBoardTiers.Of(facts), WinUiHomeTiers.Of(facts), firstWidth), static () => new BoardView())
            with { Key = "home:board-root" };
    }

    internal sealed record BoardProps(HomeBoardProps Board, WinUiHomeBoardLayout Lay, WinUiHomeLayout Home, float FirstWidth);

    /// <summary>The board's component: its width, the measured full rows, the items memo and the reorder.</summary>
    internal sealed class BoardView : Component
    {
        Memo<PackMetrics>? _metrics;
        IReadSignal<float> _width = s_noWidth;
        static readonly IReadSignal<float> s_noWidth = new Signal<float>(0f);
        readonly Dictionary<string, Signal<float>> _extent = new(StringComparer.Ordinal);
        readonly Action<RectF> _onBand;
        float _bandH;
        BoardProps? _p;
        PackReorder<WinUiBoardItem>? _reorder;
        PackGridOptions<WinUiBoardItem>? _options;
        Func<WinUiBoardItem, PackCell, Element>? _template;
        Func<PackBox, HomeTileForm>? _formRule;
        ShortcutGridSpec? _shortcuts;

        static readonly Func<PackBox, HomeTileForm> s_small = static _ => HomeTileForm.S;

        static readonly Func<PackBox, HomeTileForm> s_full = static _ => HomeTileForm.Full;

        public BoardView()
        {
            _onBand = r => _bandH = r.H;
        }

        /// <summary>A full row's measured height (0 until its body has laid out once).</summary>
        Signal<float> ExtentOf(string id)
        {
            if (!_extent.TryGetValue(id, out var s)) _extent[id] = s = new Signal<float>(0f);
            return s;
        }

        /// <summary>The board's width: measured, else the region's estimate (the first frame).</summary>
        float WidthNow(float measured) => measured > 0f ? measured : _p?.FirstWidth ?? 0f;


        // A measured body lands on its box at the top of the NEXT frame (Measured): the last height a frame laid it out at is the one it
        // presents. A body whose height eases (RowsReflow) is first laid out at its new height and only then put back at the ease's start, so
        // a height written at once would pack every row under it at the far height for one frame and then pull them back.
        readonly Dictionary<string, float> _measured = new(StringComparer.Ordinal);
        // The rows whose box follows their body: a body's FIRST height re-packs the board with its re-flow (the estimate it mounted at was
        // the rules' guess); every later one is the body's own (eased) height, which the box follows frame by frame.
        readonly HashSet<string> _follows = new(StringComparer.Ordinal);
        Action<Action>? _post;
        Action? _applyMeasured;
        bool _measurePosted;

        /// <summary>A full row's body laid out at <paramref name="h"/>. Its box follows it frame by frame (the row packs with
        /// <see cref="PackFlags.Follows"/>): the body eases its own height (<see cref="WinUiDiscoveryMotion.RowsReflow"/>), through a window drag
        /// too, so every row under it rides the ease. The first height a body reports lands at once.</summary>
        void Measured(Signal<float> extent, string id, float h)
        {
            if (h <= 0f) return;
            if (extent.Peek() <= 0f) { extent.Value = h; return; }
            _measured[id] = h;
            if (_measurePosted || _post is null) return;
            _measurePosted = true;
            _post(_applyMeasured ??= ApplyMeasured);
        }

        static string HeadId(BoardEntry e) => "group:" + e.GroupId;

        /// <summary>A group's head at its tier's height, which eases when the tier changes it (<see cref="WinUiDiscoveryMotion.RowsReflow"/>, through a
        /// window drag too); the packed head follows it (<see cref="Measured"/>).</summary>
        Element EasedHead(BoardEntry e, float height, Element head)
        {
            var extent = ExtentOf(HeadId(e));
            string id = HeadId(e);
            return new BoxEl
            {
                Direction = 1, MinWidth = 0f, Height = height, Shrink = 0f, AlignItems = FlexAlign.Stretch,
                Layout = Design.Reduced ? null : WinUiDiscoveryMotion.RowsReflow,
                OnBoundsChanged = r => Measured(extent, id, r.H),
                Children = [head],
            };
        }

        void ApplyMeasured()
        {
            _measurePosted = false;
            foreach (var (id, h) in _measured)
            {
                var s = ExtentOf(id);
                if (MathF.Abs(h - s.Peek()) > WinUiHomeBoardRules.ExtentSlop) { s.Value = h; _follows.Add(id); }
            }
            _measured.Clear();
        }

        public override Element Render()
        {
            var p = UseProps<BoardProps>();
            _p = p;
            // The board's STRUCTURE width (the columns, the forms, the shortcuts' grid): the width the board lands at while a width reflow (the page's
            // margins easing at a tier commit) resizes it, so every choice is made once per slide instead of re-made at each width the slide
            // passes (a column count flipping mid-ease re-flowed the whole board under the slide). Sizes follow the live width: the grid measures its own.
            // Before the first measure every read goes through WidthNow: the region's estimate of the board's width (FirstWidth).
            _width = UseStructureWidth();
            // The pack's metrics are discrete (the columns): read through a computed, so a width that changes no column (every frame of a drag)
            // does not re-render the grid; the grid packs to its own measured width.
            _metrics = UseComputed(() => _p!.Lay.Metrics(WidthNow(_width.Value)));
            var board = p.Board;
            board.View.NoEditMode = "winui has no edit mode";   // Windows 11's board changes one thing at a time, on the thing itself
            var items = UseComputed(() => ItemsOf(_p!.Board.Model.Value));
            _post = UsePost();

            _formRule ??= box => WinUiHomeBoardRules.FormOf(in box, _p!.Lay, _p.Lay.ColumnsAt(WidthNow(_width.Peek())));
            _template ??= (item, cell) => Item(item, cell);
            _reorder ??= new PackReorder<WinUiBoardItem>("home.tile")
            {
                // A shortcut cell showing Recently played is not the shortcut it stands for: it does not lift until the grid shows the shortcuts again.
                // The shortcuts grid is the Pinned group's whole body: its cells move inside it, the grid itself stays.
                CanLift = it => !it.EmptyRow && it.Entry.Kind != BoardEntryKind.GroupHead && !HomeShortcuts.IsGrid(it.Entry),
                LiftStyle = new DragVisualStyle { Scale = 1.03f },
                Slot = static rect => WinUiHomeTiles.Slot(rect),
                AcceptKinds = [WinUiHomeAddBand.DragKind],
                CanAccept = static payload => payload is HomeCandidate,
                // A candidate dragged in from the add band: a provisional tile at its chosen size under the pointer; the drop adds it there.
                Materialize = static payload => payload is HomeCandidate c ? Provisional(c) : null,
                OnCommit = m => Commit(m),
                OnForeignCommit = (payload, index) => ForeignCommit(payload, index),
                AnnounceText = a => Announce(a),
            };
            _options ??= new PackGridOptions<WinUiBoardItem>
            {
                KeyOf = static it => it.Key,
                EntryOf = it => EntryOf(it),
                Metrics = () => _metrics!.Value,
                Controller = board.Grid,
                Scroll = board.Scroll,
                Reorder = _reorder,
                Automation = it => Automation(it),
                Key = HomeBoardKeys.Board,
            };

            bool bandOpen = board.View.Add.Value.Kind == HomeAddSurfaceKind.Band;
            return new BoxEl
            {
                Key = "home:board-col", Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
                OnKeyDown = OnKey,
                // A card's menu: a right-click opens the menu of the tile under the pointer, the Menu key / Shift+F10 that of the tile in focus
                // (its wrapper holds focus). A touch hold never arrives here: the wrapper's hold takes it, so the hold can become a drag.
                OnContextRequested = OnContext,
                Children =
                [
                    WinUiHomeAddBand.Disclosure(board, p.Lay, bandOpen) with { OnBoundsChanged = _onBand },
                    PackGrid.Create(items, _template, _options),
                ],
            };
        }

        static WinUiBoardItem Provisional(HomeCandidate c)
        {
            var size = WinUiHomeAddBand.SizeChosen(c);
            var e = new BoardEntry("provisional:" + c.Id, BoardEntryKind.Item, c.TargetGroupId, HomeSourceRegistry.Default.Of(c.Source), c.Source,
                size, SectionTemplate.None, c.Title, false, false, false, c.Source is PinSource, false, -1);
            return new WinUiBoardItem(HomeBoardKeys.Tile(e.Id), e, false);
        }

        static IReadOnlyList<WinUiBoardItem> ItemsOf(BoardModel m)
        {
            var e = m.Entries;
            var list = new List<WinUiBoardItem>(e.Length + 1);
            bool sw = HomeShortcuts.HasSwitch(m);
            bool grid = false;
            for (int i = 0; i < e.Length; i++)
            {
                var x = e[i];
                // The shortcuts (the pins, the fill, the history tile) are ONE item, the grid, where the first of them stands.
                if (HomeShortcuts.IsGridCell(x))
                {
                    if (grid) continue;
                    grid = true;
                    x = HomeShortcuts.GridEntry(x);
                }
                list.Add(new WinUiBoardItem(x.Kind == BoardEntryKind.GroupHead ? HomeBoardKeys.Group(x.GroupId) : HomeBoardKeys.Tile(x.Id), x, false,
                                            sw && x.GroupId == HomeDocs.Pinned));
                // An empty Pinned group says how to pin (spec 8.1 "Empty states"): a dashed row at the group's height.
                if (x.Kind == BoardEntryKind.GroupHead && x.GroupId == "pinned" && (i + 1 >= e.Length || e[i + 1].Kind == BoardEntryKind.GroupHead))
                    list.Add(new WinUiBoardItem("home:empty:" + x.GroupId, x, true));
            }
            return list;
        }

        PackEntry EntryOf(WinUiBoardItem it)
        {
            var lay = _p!.Lay;
            if (it.EmptyRow) return new PackEntry(PackEntryKind.Row, Extent: lay.EmptyRow);
            var e = it.Entry;
            if (e.Kind == BoardEntryKind.GroupHead)
            {
                // A group's head steps its height with the tier (the shortcuts head 56 -> 96 at Compact): the head eases there (EasedHead) and the
                // rows under it follow it frame by frame.
                var head = WinUiHomeBoardRules.EntryOf(e, lay, 0f, 0f, false);
                float eased = ExtentOf(HeadId(e)).Value;
                return eased > 0f && _follows.Contains(HeadId(e)) ? head with { Extent = eased, Flags = head.Flags | PackFlags.Follows } : head;
            }
            float open = _p.Board.View.OpenId.Value == e.Id ? WinUiHomeBoardRules.OpenExtent(e, e.Source, lay) : 0f;
            float full = 0f;
            bool measured = false;
            if (WinUiHomeBoardRules.SizeOf(e) == HomeSize.Full)
            {
                // A full row packs at its chapter's own height (the numbers its body reserves with), so it mounts in its box; once its body has
                // laid out, the row follows the body's height (which eases itself).
                full = ExtentOf(e.Id).Value;
                measured = full > 0f;
                if (!measured)
                    full = HomeShortcuts.IsGrid(e)
                        ? ShortcutGrid.Extent(_p.Board.Model.Value, Shortcuts(), WidthNow(_width.Value))
                        : FullEstimate(e, _p.Home, WidthNow(_width.Value), lay);
            }
            var entry = WinUiHomeBoardRules.EntryOf(e, lay, full, open, false);
            return measured && _follows.Contains(e.Id) && open <= 0f && entry.Kind == PackEntryKind.Band ? entry with { Flags = entry.Flags | PackFlags.Follows } : entry;
        }

        /// <summary>A full row's height before its body has laid out: its chapter's own extent at the row's content width (the ⋯ lane aside), else
        /// the rules' fallback until it is measured.</summary>
        static float FullEstimate(BoardEntry e, WinUiHomeLayout home, float rowW, WinUiHomeBoardLayout lay)
        {
            Zone? zone = e.Raw switch
            {
                WidgetSource { Widget: "daylist" } => HomeBoardZones.DaylistZone(e),
                WidgetSource { Widget: "recents" } => HomeBoardZones.RecentsZone(e),
                WidgetSource { Widget: "releases" } => HomeBoardZones.ReleasesZone(e),
                _ => e.Zone,
            };
            // A chapter with a header keeps the row's full width (the menu button rides its header); a headerless one gives up the lane.
            float contentW = zone is not null && WinUiHomeZones.HasHeader(zone.Kind) ? rowW : rowW - Spacing.XS - lay.MoreBox;
            float h = zone is null ? 0f : WinUiHomeZones.ChapterExtent(zone, home, contentW);
            return h > 0f ? h : WinUiHomeBoardRules.FullFallback;
        }

        Element Item(WinUiBoardItem it, PackCell cell)
        {
            var board = _p!.Board;
            if (it.EmptyRow) return WinUiHomeTiles.EmptyPinned(_p.Lay);
            var e = it.Entry;
            if (e.Kind == BoardEntryKind.GroupHead)
                return EasedHead(e, WinUiHomeBoardRules.EntryOf(e, _p.Lay, 0f, 0f, false).Extent,
                    e.GroupId == HomeDocs.Pinned ? WinUiHomeTiles.ShortcutsHead(e, _p.Lay, _p.Home, board, it.Switch) : WinUiHomeTiles.GroupHead(e, _p.Lay));
            if (board.View.OpenId.Value == e.Id)
                return WinUiHomeTiles.Open(e, board.Options(e));
            // A full row always draws its full form (its height is its body's); every other size draws what its box can carry.
            if (WinUiHomeBoardRules.SizeOf(e) != HomeSize.Full) return board.Tile(e, cell, _formRule!);
            var tile = HomeShortcuts.IsGrid(e) ? ShortcutGrid.Create(board, Shortcuts(), WidthNow(_width.Peek())) : board.Tile(e, cell, s_full);
            // A full row: the body lays out at its own height inside the packed box, and that height becomes the box (by the re-flow).
            var extent = ExtentOf(e.Id);
            string id = e.Id;
            return new BoxEl
            {
                Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 1, MinWidth = 0f, Grow = 0f, Shrink = 0f, AlignItems = FlexAlign.Stretch,
                        // A body whose rows change with the width (a grid gaining or losing a column, a shelf's cards stepping a size) eases to its
                        // new height through real layout, so the box measured here and the rows under it ride the ease instead of stepping.
                        Layout = Design.Reduced ? null : WinUiDiscoveryMotion.RowsReflow,
                        // The box follows the height the body lays out at (Measured).
                        OnBoundsChanged = r => Measured(extent, id, r.H),
                        Children = [tile],
                    },
                ],
            };
        }

        /// <summary>WinUI's shortcuts grid: the tier's columns, a 64 row (a 48 cover), the board's gap; each cell its pin's row tile (the Recently played
        /// item at its place while the head's switch shows that face); a cell always drags (WinUI has no edit mode); the strip's own edge chevrons on
        /// hover, the pips under it.</summary>
        ShortcutGridSpec Shortcuts()
        {
            var p = _p!;
            if (_shortcuts is { } s && ReferenceEquals(s_specLay, p.Lay)) return s;
            s_specLay = p.Lay;
            var lay = p.Lay;
            var board = p.Board;
            return _shortcuts = new ShortcutGridSpec(
                Columns: w => lay.ShortcutCols.Columns(w, lay.Gap),
                Unit: _ => lay.ShortcutUnit,
                Gap: lay.Gap,
                Cell: (e, index, cell) => HomeShortcuts.HasSwitch(board.Model.Peek())
                    ? WinUiHomeTiles.ShortcutCell(board.Tile(e, cell, s_small), index, board)
                    : board.Tile(e, cell, s_small),
                CanReorder: static () => true,
                // FlipView's hover-revealed edge buttons over the pages.
                Edge: static (prev, pager) => IconButton.Create(prev ? Icons.ChevronLeft : Icons.ChevronRight, prev ? pager.Prev : pager.Next)
                    with { Key = prev ? "home:shortcuts-edge:prev" : "home:shortcuts-edge:next" },
                Below: static c => Embed.Comp(() => new WinUiHomeZones.ClusterPips(c)) with { Key = "home:shortcuts-pips" },
                BelowH: lay.ShortcutPipsH);
        }

        WinUiHomeBoardLayout? s_specLay;

        void Commit(PackMove m)
        {
            var board = _p!.Board;
            var items = ItemsOf(board.Model.Peek());
            if ((uint)m.To >= (uint)items.Count) return;
            string id = IdOf(m.Key);
            var target = items[m.To];
            if (target.EmptyRow || target.Entry.Kind == BoardEntryKind.GroupHead && m.To < m.From) return;
            if (id.Length > 0 && target.Entry.Id != id) board.Verbs.MoveOnto(id, target.Entry.Id);
        }

        void ForeignCommit(object? payload, int index)
        {
            if (payload is not HomeCandidate c) return;
            var board = _p!.Board;
            var entries = board.Model.Peek().Entries;
            var items = ItemsOf(board.Model.Peek());
            int at = -1;
            if ((uint)index < (uint)items.Count && !items[index].EmptyRow)
                for (int i = 0; i < entries.Length; i++) if (entries[i].Id == items[index].Entry.Id) { at = i; break; }
            board.Verbs.Add(c, at, WinUiHomeAddBand.SizeChosen(c));
        }

        string? Announce(PackAnnounce a)
        {
            var board = _p!.Board;
            string id = IdOf(a.Key);
            string name = NameOf(board, id);
            return a.Kind switch
            {
                PackAnnounceKind.Grab => Strings.Theme.Winui.Home.Board.Grabbed(name: name),
                // The projected place while lifted (the document has not moved yet); the drop itself is the move verb's sentence.
                PackAnnounceKind.Move => Strings.Home.BoardVerbs.Position(n: a.Position, count: a.Count, group: GroupTitleAt(board, a.Group)),
                PackAnnounceKind.Cancel => Strings.Theme.Winui.Home.Board.Cancelled(name: name),
                _ => null,
            };
        }

        /// <summary>The title of the group the lifted tile is projected into (the pack's group ordinal: the n-th group head in order), not the
        /// group it left.</summary>
        static string GroupTitleAt(HomeBoardProps board, int group)
        {
            var entries = board.Model.Peek().Entries;
            int n = -1;
            for (int i = 0; i < entries.Length; i++)
                if (entries[i].Kind == BoardEntryKind.GroupHead && ++n == group) return entries[i].Title;
            return "";
        }

        PackAutomation Automation(WinUiBoardItem it)
        {
            var e = it.Entry;
            return new PackAutomation(it.EmptyRow ? Loc.Get(Strings.Theme.Winui.Home.Board.EmptyPinned) : e.Title,
                                      e.Kind == BoardEntryKind.GroupHead || it.EmptyRow ? AutomationRole.Text : AutomationRole.Button, 0, 0);
        }

        static string NameOf(HomeBoardProps board, string id)
        {
            var entries = board.Model.Peek().Entries;
            for (int i = 0; i < entries.Length; i++) if (entries[i].Id == id) return entries[i].Title;
            return "";
        }

        /// <summary>The board's keys while focus is inside Home's board or its head tools (single keys stand aside for a text editor): Ctrl+Z
        /// undoes; "/" opens the add band's search; Escape unwinds (the open tile, then the band).</summary>
        internal static void HandleKey(KeyEventArgs e, HomeBoardProps board)
        {
            if (e.Handled) return;
            bool editor = board.EditorFocused();
            if (!editor && e.Ctrl && !e.Shift && !e.Alt && e.KeyCode == Keys.Z) { board.Verbs.Undo(); e.Handled = true; return; }
            if (editor && e.KeyCode != Keys.Escape) return;
            // The focused tile (the grid's wrapper holds focus; plain arrows move focus there, Alt+Shift+arrows bubble here): Start's chord moves it,
            // the Menu key or Shift+F10 opens its menu.
            if (board.Grid.FocusedKey is { } fk && IdOf(fk) is { Length: > 0 } id)
            {
                if (e.Alt && e.Shift && !e.Ctrl)
                {
                    switch (e.KeyCode)
                    {
                        case Keys.Left: board.Verbs.MoveBy(id, -1); e.Handled = true; return;
                        case Keys.Right: board.Verbs.MoveBy(id, 1); e.Handled = true; return;
                        case Keys.Up or Keys.Down:
                            if (board.Grid.Neighbour(fk, 0, e.KeyCode == Keys.Up ? -1 : 1) is { } nk && IdOf(nk) is { Length: > 0 } target)
                                board.Verbs.MoveOnto(id, target);
                            e.Handled = true;
                            return;
                    }
                }
                if (e.KeyCode == Keys.Apps || e.Shift && !e.Ctrl && !e.Alt && e.KeyCode == Keys.F10)
                {
                    if (WinUiHomeTiles.TileMore.OpenFor(id)) e.Handled = true;
                    return;
                }
            }
            if (!e.Ctrl && !e.Alt && e.KeyCode == WinUiHomeAddBand.SlashKey)
            {
                WinUiHomeAddBand.OpenSearch(board);
                e.Handled = true;
                return;
            }
            if (e.KeyCode == Keys.Escape)
            {
                if (board.View.OpenId.Peek() is not null) { board.Verbs.Close(refocus: true); e.Handled = true; return; }
                if (board.View.Add.Peek().Kind == HomeAddSurfaceKind.Band) { WinUiHomeAddBand.Close(board); e.Handled = true; }
            }
        }

        internal static string IdOf(string key) => key.StartsWith("home:tile:", StringComparison.Ordinal) ? key["home:tile:".Length..] : "";

        /// <summary>The board's keys while focus is inside it (guarded by the text-editor test): Ctrl+Z undoes; "/" opens the add band's search;
        /// Escape unwinds (the open tile, then the band).</summary>
        void OnContext(ContextRequestEventArgs e)
        {
            if (_p is not { } p) return;
            var grid = p.Board.Grid;
            // The position is local to this column: the grid stands under the add band.
            string? key = e.Trigger == ContextRequestTrigger.Keyboard ? grid.FocusedKey : grid.KeyAt(e.Position.X, e.Position.Y - _bandH);
            if (key is not null && IdOf(key) is { Length: > 0 } id) WinUiHomeTiles.TileMore.OpenFor(id);
        }

        void OnKey(KeyEventArgs e)
        {
            if (_p is not null) HandleKey(e, _p.Board);
        }
    }
}
