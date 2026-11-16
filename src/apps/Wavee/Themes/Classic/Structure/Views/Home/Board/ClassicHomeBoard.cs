// ── Themes/Classic/Structure/Views/Home/Board/ClassicHomeBoard.cs — Classic's `Home.Board` (pinned-home-tech.md 1.7; spec 8.3) ─────────────────
//
// Wavee's own: Edit Home, ON THE TILES. At rest the board is speed-dial cells, cards and shelves under 3 group titles; Home's ⋯ (in the facet row's end,
// `ClassicHomeEditBar`) and a tile's menu and the "Customize Home" link at the end enter Edit Home. Then the ⋯'s slot holds the sidebar's edit bar,
// every tile gets a dashed outline and its chips in a 40 DIP strip of its own above it (`ClassicHomeChips`; the rows grow by it, by the grid's re-flow),
// every group ends in an "Add to {group}" row (`ClassicHomeAddRow`), and the tiles take focus: Space grabs, arrows move, Space drops, Esc cancels
// (`PackKeyboard.LiftMode`); Alt+↑/↓ one step, Alt+Shift+↑/↓ to the group's edge, Delete hides or unpins, Ctrl+Z / Ctrl+Y. The nav pane never changes.
//
//   Home.Board
//   └─ column  (keys; the board's width for its column count)
//      ├─ the find strip   sticky under the facet band: Compact's find field and the results of "Find something to add" (`ClassicHomeFind`)
//      └─ PackGrid         keyed `home:board`; group titles, tiles `home:tile:<id>`, the add rows, the empty Pinned drop row, the end link
//
// THE BOX IS THE PACK'S: a tile's rect is f(size class, column count, the group's fill, Edit Home's strip) and nothing a tile loads. A shelf's height is
// its chapter's: it packs at the rules' estimate, then at the chapter's measured height, and the wrapper follows by the grid's 250 ms re-flow.

using System.Collections.Generic;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Input;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.HomeUi;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>What a row of Classic's grid is beside a board entry.</summary>
internal enum ClassicBoardRow : byte
{
    /// <summary>A board entry (a group title, a tile).</summary>
    Entry,

    /// <summary>The empty Pinned group's drop row.</summary>
    Empty,

    /// <summary>A group's "Add to {group}" row (Edit Home).</summary>
    Add,

    /// <summary>"Customize Home" at the board's end (at rest).</summary>
    End,
}

/// <summary>What the Classic grid packs. <see cref="Cell"/> is a shortcut cell's place in the shortcuts grid (the Recently played item that face shows
/// there), -1 for everything else; <see cref="Switch"/> says the Pinned group carries the history tile (<see cref="HomeShortcuts"/>).</summary>
internal sealed record ClassicBoardItem(string Key, BoardEntry Entry, ClassicBoardRow Row, int Cell = -1, bool Switch = false);

/// <summary>Classic's pinned Home board.</summary>
internal static class ClassicHomeBoard
{
    /// <summary>The registered template of <see cref="ViewKeys.HomeBoard"/>.</summary>
    public static Element Template(HomeBoardProps props, in ViewContext ctx)
    {
        var facts = ctx.Tier();
        return Embed.Comp(new BoardProps(props, ClassicHomeBoardTiers.Of(facts), ClassicHomeTiers.Of(facts)), static () => new BoardView())
            with { Key = "home:board-root" };
    }

    internal sealed record BoardProps(HomeBoardProps Board, ClassicHomeBoardLayout Lay, ClassicHomeLayout Home);

    static readonly Signal<float> s_width = new(0f);

    /// <summary>The mounted board's width (the edit bar in the facet row reads it: its caption and find field stand inline only where the row has room).</summary>
    internal static IReadSignal<float> Width => s_width;

    /// <summary>The live board (the edit bar in the facet row reaches it: Edit Home's entry and exit move focus between the two).</summary>
    internal static BoardView? Live { get; private set; }

    internal static string IdOf(string key) => key.StartsWith("home:tile:", StringComparison.Ordinal) ? key["home:tile:".Length..] : "";

    static string GroupTitle(BoardModel m, string groupId)
    {
        foreach (var e in m.Entries) if (e.Kind == BoardEntryKind.GroupHead && e.GroupId == groupId) return e.Title;
        return "";
    }

    /// <summary>The board's component: its width, the measured shelves, the items memo, the reorder, the keys and the inspector's seam.</summary>
    internal sealed class BoardView : Component, IHomeBoardInspect
    {
        readonly Signal<float> _width = new(0f);
        readonly Dictionary<string, Signal<float>> _extent = new(StringComparer.Ordinal);
        readonly Action<RectF> _onBounds;
        BoardProps? _p;
        PackReorder<ClassicBoardItem>? _reorder;
        PackGridOptions<ClassicBoardItem>? _options;
        Func<ClassicBoardItem, PackCell, Element>? _template;
        Func<PackBox, HomeTileForm>? _formRule;

        static readonly Func<PackBox, HomeTileForm> s_full = static _ => HomeTileForm.Full;

        public BoardView()
        {
            _onBounds = r =>
            {
                if (r.W > 0f && MathF.Abs(r.W - _width.Peek()) > 0.25f) _width.Value = r.W;
                if (r.W > 0f && MathF.Abs(r.W - s_width.Peek()) > 0.25f) s_width.Value = r.W;
            };
        }

        internal HomeBoardProps Board => _p!.Board;

        Signal<float> ExtentOf(string id)
        {
            if (!_extent.TryGetValue(id, out var s)) _extent[id] = s = new Signal<float>(0f);
            return s;
        }

        public override Element Render()
        {
            var p = UseProps<BoardProps>();
            _p = p;
            var board = p.Board;
            var items = UseComputed(() =>
            {
                var list = ItemsOf(_p!.Board.Model.Value, _p.Board.View.Editing.Value, _p.Board.ReadOnly);
                // A re-projection that changes no row while a tile is lifted (a feed landing, a table filling in) keeps the list the lift was
                // made on: a new list under a lift cancels it.
                if (_reorder?.IsLifted == true && _lastItems is { } prev && SameRows(prev, list)) return prev;
                _lastItems = list;
                return list;
            });
            // The core mount (`HomeBoardMount`) registers the generic seam in its layout effect; Classic's board registers its own after it (an
            // effect runs after every layout effect), so `home do=edit|surface|search|reset` drive Classic's Edit Home, its find and its question.
            UseEffect(() =>
            {
                Live = this;
                HomeBoardInspect.Register(this);
                HomeBoardInspect.RegisterPack(HomeBoardKeys.Board, _p!.Board.Grid);
                var grid = _p.Board.Grid;
                return () =>
                {
                    if (ReferenceEquals(Live, this)) Live = null;
                    HomeBoardInspect.Unregister(this);
                    HomeBoardInspect.UnregisterPack(grid);
                };
            }, DepKey.Empty);
            // Leaving Home (the board unmounts) leaves Edit Home: the mode is view state of a visit, never saved.
            UseEffect(() => () => _p?.Board.View.Reset(), DepKey.Empty);

            _formRule ??= box => ClassicHomeBoardRules.FormOf(in box, _p!.Lay);
            _template ??= (item, cell) => Item(item, cell);
            _reorder ??= new PackReorder<ClassicBoardItem>("home.tile")
            {
                // The shortcuts grid is the Pinned group's whole body: its cells move inside it, the grid itself stays.
                CanLift = static it => it.Row == ClassicBoardRow.Entry && it.Entry.Kind != BoardEntryKind.GroupHead && !HomeShortcuts.IsGrid(it.Entry),
                Enabled = () => _p!.Board.View.Editing.Peek() && !_p.Board.ReadOnly,
                Keyboard = PackKeyboard.LiftMode,
                LiftStyle = new DragVisualStyle { Scale = 1.03f },
                Slot = static rect => ClassicHomeTiles.Slot(rect),
                OnCommit = m => Commit(m),
                AnnounceText = a => Announce(a),
            };
            _options ??= new PackGridOptions<ClassicBoardItem>
            {
                KeyOf = static it => it.Key,
                EntryOf = it => EntryOf(it),
                Metrics = () => _p!.Lay.Metrics(_width.Value, _p.Board.View.Editing.Value),
                Controller = board.Grid,
                Scroll = board.Scroll,
                Reorder = _reorder,
                // Classic's tiles take focus only in Edit Home (at rest the cards inside them are the stops).
                ItemsFocusable = () => _p!.Board.View.Editing.Value,
                Automation = it => Automation(it),
                Key = HomeBoardKeys.Board,
            };

            return new BoxEl
            {
                Key = "home:board-col", Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
                OnBoundsChanged = _onBounds,
                OnKeyDown = OnKey,
                Children =
                [
                    ClassicHomeFind.Strip(board, p.Lay),
                    // The grid's height follows its pack, which a shelf's measured height reaches a frame or more after the width changed: that
                    // height eases through real layout here, so the page below (and the page's own extent) grows and shrinks every frame instead
                    // of stepping late by the difference.
                    new BoxEl
                    {
                        Key = "home:board-height", Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
                        Layout = ClassicTierMotion.HeightReflow,
                        Children = [PackGrid.Create(items, _template, _options)],
                    },
                    Embed.Comp(new RingProps(board.View), static () => new RingClear()) with { Key = "home:ring-clear" },
                ],
            };
        }

        IReadOnlyList<ClassicBoardItem>? _lastItems;

        static bool SameRows(IReadOnlyList<ClassicBoardItem> a, IReadOnlyList<ClassicBoardItem> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                var x = a[i]; var y = b[i];
                if (x.Key != y.Key || x.Row != y.Row || x.Entry.Size != y.Entry.Size || x.Entry.Hidden != y.Entry.Hidden) return false;
            }
            return true;
        }

        /// <summary>The grid's rows: every entry; after an empty Pinned head its drop row; in Edit Home an add row at every group's end; at rest the end link.</summary>
        static IReadOnlyList<ClassicBoardItem> ItemsOf(BoardModel m, bool editing, bool readOnly)
        {
            var e = m.Entries;
            var list = new List<ClassicBoardItem>(e.Length + 6);
            BoardEntry? group = null;
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
                if (x.Kind == BoardEntryKind.GroupHead)
                {
                    if (group is not null && editing && !readOnly) list.Add(new ClassicBoardItem("home:addrow:" + group.GroupId, group, ClassicBoardRow.Add));
                    group = x;
                    list.Add(new ClassicBoardItem(HomeBoardKeys.Group(x.GroupId), x, ClassicBoardRow.Entry, Switch: sw && x.GroupId == HomeDocs.Pinned));
                    if (x.GroupId == HomeDocs.Pinned && (i + 1 >= e.Length || e[i + 1].Kind == BoardEntryKind.GroupHead))
                        list.Add(new ClassicBoardItem("home:empty:" + x.GroupId, x, ClassicBoardRow.Empty));
                    continue;
                }
                list.Add(new ClassicBoardItem(HomeBoardKeys.Tile(x.Id), x, ClassicBoardRow.Entry));
            }
            if (group is not null && editing && !readOnly) list.Add(new ClassicBoardItem("home:addrow:" + group.GroupId, group, ClassicBoardRow.Add));
            if (group is not null && !editing && !readOnly) list.Add(new ClassicBoardItem("home:end", group, ClassicBoardRow.End));
            return list;
        }

        PackEntry EntryOf(ClassicBoardItem it)
        {
            var lay = _p!.Lay;
            switch (it.Row)
            {
                case ClassicBoardRow.Empty: return new PackEntry(PackEntryKind.Row, Extent: lay.EmptyRow);
                case ClassicBoardRow.End: return new PackEntry(PackEntryKind.Row, Extent: lay.EndRow);
                case ClassicBoardRow.Add:
                {
                    var add = _p.Board.View.Add.Value;
                    bool open = add.Kind == HomeAddSurfaceKind.Row && add.Arg == it.Entry.GroupId;
                    return new PackEntry(PackEntryKind.Row, Extent: open ? lay.AddRowOpen : lay.AddRow);
                }
            }
            var e = it.Entry;
            if (e.Kind == BoardEntryKind.GroupHead) return ClassicHomeBoardRules.EntryOf(e, lay, 0f, 0f);
            // The shortcuts grid: a band at its own height (its tallest page), no chip strip (its cells carry their own chips).
            if (HomeShortcuts.IsGrid(e))
            {
                float grid = ExtentOf(e.Id).Value;
                return new PackEntry(PackEntryKind.Row, Extent: grid > 0f ? grid : ShortcutGrid.Extent(_p.Board.Model.Value, Shortcuts(), _width.Value));
            }
            float open2 = _p.Board.View.OpenId.Value == e.Id ? ClassicHomeBoardRules.OpenExtent(e.Source, lay) : 0f;
            float full = 0f;
            if (ClassicHomeBoardRules.SizeOf(e) == HomeSize.Full)
            {
                full = ExtentOf(e.Id).Value;
                if (full <= 0f) full = FullEstimate(e, _p.Home);
            }
            return ClassicHomeBoardRules.EntryOf(e, lay, full, open2);
        }

        /// <summary>A shelf's height before its chapter has laid out: the Home tier record's own numbers for the bodies it knows.</summary>
        static float FullEstimate(BoardEntry e, ClassicHomeLayout home)
        {
            float head = ClassicHomeRules.HeaderH + ClassicHomeRules.HeaderGap;
            if (e.Raw is WidgetSource w)
                switch (w.Widget)
                {
                    case "daylist": return float.IsNaN(home.Daylist.CardH) ? ClassicHomeRules.DaylistCardMinHeight : home.Daylist.CardH;
                    case "recents": return head + 2f * home.RecentsRowH + Spacing.S;
                    case "releases": return head + ClassicHomeBoardRules.ReleaseRowsEstimate * ClassicHomeRules.RowH;
                }
            return ClassicHomeBoardRules.FullFallback;
        }

        Element Item(ClassicBoardItem it, PackCell cell)
        {
            var board = _p!.Board;
            var lay = _p.Lay;
            switch (it.Row)
            {
                case ClassicBoardRow.Empty: return ClassicHomeTiles.EmptyPinned(board, lay);
                case ClassicBoardRow.End: return ClassicHomeTiles.EndLink(board);
                case ClassicBoardRow.Add: return ClassicHomeAddRow.Create(board, lay, it.Entry);
            }
            var e = it.Entry;
            if (e.Kind == BoardEntryKind.GroupHead) return ClassicHomeTiles.GroupHead(board, e, lay, it.Switch);
            if (board.View.OpenId.Value == e.Id)
                return ClassicHomeTiles.Open(board, e, cell, lay, board.Options(e));
            bool grid = HomeShortcuts.IsGrid(e);
            if (!grid && ClassicHomeBoardRules.SizeOf(e) != HomeSize.Full) return board.Tile(e, cell, _formRule!);
            var tile = grid ? ShortcutGrid.Create(board, Shortcuts(), _width.Peek()) : board.Tile(e, cell, s_full);
            // A shelf: the chapter lays out at its own height inside the packed box, and that height becomes the box (by the re-flow). The strip above it
            // in Edit Home is the tile's own (its chips), so the measure is the body under it.
            var extent = ExtentOf(e.Id);
            return new BoxEl
            {
                Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 1, MinWidth = 0f, Grow = 0f, Shrink = 0f, AlignItems = FlexAlign.Stretch,
                        OnBoundsChanged = r =>
                        {
                            float body = r.H - (cell.Box.Peek().ToolStrip);
                            if (body > 0f && MathF.Abs(body - extent.Peek()) > 0.5f) extent.Value = body;
                        },
                        Children = [tile],
                    },
                ],
            };
        }

        ShortcutGridSpec? _shortcuts;
        ClassicHomeBoardLayout? _shortcutsLay;

        static readonly Func<PackBox, HomeTileForm> s_small = static _ => HomeTileForm.S;

        /// <summary>Classic's shortcuts grid: the tier's columns, the speed-dial cell's row, the board's gap; each cell its pin's speed-dial cell (the
        /// Recently played item at its place while the head's switch shows that face); the cells lift in Edit Home; the pager is the head's.</summary>
        ShortcutGridSpec Shortcuts()
        {
            var p = _p!;
            if (_shortcuts is { } s && ReferenceEquals(_shortcutsLay, p.Lay)) return s;
            _shortcutsLay = p.Lay;
            var lay = p.Lay;
            var board = p.Board;
            return _shortcuts = new ShortcutGridSpec(
                Columns: w => lay.ShortcutCols.Columns(w, lay.Gap),
                Unit: _ => lay.ShortcutUnit,
                Gap: lay.Gap,
                Cell: (e, index, cell) => HomeShortcuts.HasSwitch(board.Model.Peek())
                    ? ClassicHomeTiles.ShortcutCell(board.Tile(e, cell, s_small), index, board, lay)
                    : board.Tile(e, cell, s_small),
                CanReorder: () => board.View.Editing.Value && !board.ReadOnly);   // a subscribing read: the page grids wire their lift when Edit Home starts
        }

        void Commit(PackMove m)
        {
            var board = _p!.Board;
            var items = ItemsOf(board.Model.Peek(), board.View.Editing.Peek(), board.ReadOnly);
            if ((uint)m.To >= (uint)items.Count) return;
            string id = IdOf(m.Key);
            var target = items[m.To];
            if (target.Row != ClassicBoardRow.Entry)
            {
                // Onto an add row or the drop row: the end of that group (after its last tile, or after its head when it has none).
                int k = m.To;
                while (k > 0 && items[k].Row != ClassicBoardRow.Entry) k--;
                target = items[k];
            }
            if (target.Entry.Kind == BoardEntryKind.GroupHead && m.To < m.From) return;
            if (id.Length > 0 && target.Entry.Id != id) board.Verbs.MoveOnto(id, target.Entry.Kind == BoardEntryKind.GroupHead ? target.Entry.GroupId : target.Entry.Id);
        }

        string? Announce(PackAnnounce a)
        {
            var board = _p!.Board;
            string id = IdOf(a.Key);
            string name = NameOf(board, id);
            // While lifted the place is the projection's (the document moves only at the drop): the pack's own position and count in its group.
            string where = a.Position > 0 ? Strings.Home.BoardVerbs.Position(n: a.Position, count: a.Count, group: GroupTitleAt(board.Model.Peek(), a.Group)) : "";
            return a.Kind switch
            {
                PackAnnounceKind.Grab => Strings.Theme.Classic.Home.Board.Grabbed(name: name, where: where),
                PackAnnounceKind.Move => where,
                // The drop's sentence is the verb's own ("Moved {name}, position n of m in {group}"), said once the document has moved.
                PackAnnounceKind.Drop => null,
                PackAnnounceKind.Cancel => Loc.Get(Strings.Theme.Classic.Home.Board.Cancelled),
                _ => null,
            };
        }

        static string GroupTitleAt(BoardModel m, int ordinal)
        {
            int g = -1;
            foreach (var e in m.Entries)
                if (e.Kind == BoardEntryKind.GroupHead && ++g == ordinal) return e.Title;
            return "";
        }

        PackAutomation Automation(ClassicBoardItem it)
        {
            var e = it.Entry;
            string name = it.Row switch
            {
                ClassicBoardRow.Empty => Loc.Get(Strings.Theme.Classic.Home.Board.EmptyPinned),
                ClassicBoardRow.Add => Strings.Theme.Classic.Home.Board.AddTo(group: e.Title),
                ClassicBoardRow.End => Loc.Get(Strings.Theme.Classic.Home.Board.CustomizeHome),
                _ => e.Title,
            };
            bool text = it.Row is ClassicBoardRow.Empty || e.Kind == BoardEntryKind.GroupHead && it.Row == ClassicBoardRow.Entry;
            return new PackAutomation(name, text ? AutomationRole.Text : AutomationRole.Button, 0, 0);
        }

        static string NameOf(HomeBoardProps board, string id)
        {
            var entries = board.Model.Peek().Entries;
            for (int i = 0; i < entries.Length; i++) if (entries[i].Id == id) return entries[i].Title;
            return "";
        }

        // ══ Edit Home ════════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>Enter or leave Edit Home. Entering puts focus on the tile it came from (or the first); leaving folds every strip, closes the open tile
        /// and the question, and returns focus to Home's ⋯.</summary>
        internal void SetEditing(bool on, string? fromId = null)
        {
            if (_p is not { } p) return;
            var board = p.Board;
            var view = board.View;
            if (on && board.ReadOnly) return;
            if (view.Editing.Peek() == on) { if (on && fromId is not null) board.Grid.Focus(HomeBoardKeys.Tile(fromId)); return; }
            if (!on)
            {
                if (_reorder?.IsLifted == true) _reorder.Drop();
                board.Verbs.Close(refocus: false);
                view.ConfirmingReset.Value = false;
                view.Add.Value = HomeAddSurface.Closed;
                view.Query.Value = "";
                view.Editing.Value = false;
                board.Verbs.Say(Loc.Get(Strings.Theme.Classic.Home.Board.DoneSaid));
                ClassicHomeEditBar.FocusMore();
                return;
            }
            view.Editing.Value = true;
            board.Verbs.Say(Loc.Get(Strings.Theme.Classic.Home.Board.Editing));
            string? first = fromId;
            if (first is null)
                foreach (var e in board.Model.Peek().Entries)
                    if (e.Kind != BoardEntryKind.GroupHead && !e.Hidden) { first = e.Id; break; }
            if (first is not null) board.Grid.Focus(HomeBoardKeys.Tile(first));
        }

        /// <summary>The board's keys while focus is inside it (the single keys stand aside for a text editor): Ctrl+Z / Ctrl+Y; in Edit Home "/" to the
        /// find field, Delete, Alt+↑/↓ and Alt+Shift+↑/↓ on the focused tile; Escape unwinds (the open tile, the question, an add surface, the mode).</summary>
        void OnKey(KeyEventArgs e)
        {
            if (e.Handled || _p is null) return;
            var board = _p.Board;
            var view = board.View;
            if (e.Ctrl && !e.Alt && e.KeyCode == Keys.Z && !e.Shift) { board.Verbs.Undo(); e.Handled = true; return; }
            if (e.Ctrl && !e.Alt && (e.KeyCode == Keys.Y || e.KeyCode == Keys.Z && e.Shift)) { board.Verbs.Redo(); e.Handled = true; return; }
            if (!view.Editing.Peek()) return;
            if (e.KeyCode == Keys.Escape) { if (Unwind()) e.Handled = true; return; }
            if (board.EditorFocused()) return;
            string? id = board.Grid.FocusedKey is { } k ? IdOf(k) : null;
            if (id is { Length: 0 }) id = null;
            if (!e.Ctrl && !e.Alt && e.KeyCode == ClassicHomeBoardRules.SlashKey) { ClassicHomeEditBar.FocusFind(); e.Handled = true; return; }
            if (id is null) return;
            if (!e.Ctrl && !e.Alt && !e.Shift && e.KeyCode == ClassicHomeBoardRules.DeleteKey) { board.Verbs.Remove(id); e.Handled = true; return; }
            if (e.Alt && !e.Ctrl && (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down))
            {
                bool up = e.KeyCode == Keys.Up;
                if (e.Shift) board.Verbs.MoveToEdge(id, toStart: up);
                else board.Verbs.MoveBy(id, up ? -1 : 1);
                board.Grid.Focus(HomeBoardKeys.Tile(id));
                e.Handled = true;
            }
        }

        /// <summary>One Escape: the open tile, then the reset question, then an add row or the find strip, then the mode.</summary>
        internal bool Unwind()
        {
            if (_p is null) return false;
            var board = _p.Board;
            var view = board.View;
            if (view.OpenId.Peek() is not null) { board.Verbs.Close(refocus: true); return true; }
            if (view.ConfirmingReset.Peek()) { view.ConfirmingReset.Value = false; return true; }
            if (view.Add.Peek().IsOpen || view.Query.Peek().Length > 0)
            {
                var was = view.Add.Peek();
                view.Add.Value = HomeAddSurface.Closed;
                view.Query.Value = "";
                if (was.Kind == HomeAddSurfaceKind.Row) ClassicHomeAddRow.FocusButton(was.Arg);
                return true;
            }
            if (view.Editing.Peek()) { SetEditing(false); return true; }
            return false;
        }

        // ══ IHomeBoardInspect: the inspector's `home do=…` drives the same verbs and view state ═════════════════════════

        HomeBoardView V => _p!.Board.View;
        IHomeBoardVerbs Vb => _p!.Board.Verbs;

        public string Theme => "classic";
        public bool Editing => V.Editing.Peek();
        public string? SelectedId => V.SelectedId.Peek();
        public string? OpenId => V.OpenId.Peek();
        public string AddSurface => V.Add.Peek().Kind switch
        {
            HomeAddSurfaceKind.Row => "row", HomeAddSurfaceKind.Find => "find", HomeAddSurfaceKind.Band => "band",
            HomeAddSurfaceKind.AllItems => "allitems", _ => "closed",
        };
        public string AddKind => V.Add.Peek().Arg;
        public string Query => V.Query.Peek();
        public IReadOnlyList<HomeCandidate> Results => ClassicHomeFind.Results(_p!.Board);
        public BoardModel Model => _p!.Board.Model.Peek();
        public int Columns => _p!.Board.Grid.Layout.Columns;

        public bool TryRect(string entryId, out RectF rect) => _p!.Board.Grid.TryRect(HomeBoardKeys.Tile(entryId), out rect);
        public bool IsRealized(string entryId) => _p!.Board.Grid.IsRealized(HomeBoardKeys.Tile(entryId));

        public bool TrySpan(string entryId, out int cols, out float rows, out string form)
        {
            cols = 0; rows = 0f; form = "";
            var grid = _p!.Board.Grid;
            var r = grid.Layout;
            string key = HomeBoardKeys.Tile(entryId);
            for (int i = 0; i < r.Count; i++)
            {
                if (grid.KeyOf(i) != key) continue;
                cols = r.SpanCols[i];
                rows = r.SpanRows[i];
                var e = Model.Entries[Math.Max(0, Model.IndexOf(entryId))];
                form = ClassicHomeBoardRules.SizeOf(e) == HomeSize.Full ? "full"
                    : ClassicHomeBoardRules.FormOf(new PackBox(cols, rows, r.Rects[i].W, r.Rects[i].H, r.ToolStripOf[i]), _p.Lay).ToString().ToLowerInvariant();
                return true;
            }
            return false;
        }

        public int Verify(List<string> faults)
        {
            var grid = _p!.Board.Grid;
            Span<PackFault> buf = stackalloc PackFault[32];
            int n = grid.Verify(buf);
            for (int i = 0; i < n && i < buf.Length; i++)
                faults.Add(buf[i].Kind + " group " + buf[i].Group + " band " + buf[i].Band + " entry " + buf[i].Entry + " at " + buf[i].Where);
            if (grid.TryOrigin(out var o))
            {
                var r = grid.Layout;
                for (int i = 0; i < r.Count; i++)
                {
                    if (r.BandOf[i] < 0 || grid.KeyOf(i) is not { } key || !grid.TryNodeRect(key, out var node)) continue;
                    var q = r.Rects[i];
                    if (MathF.Abs(node.X - (o.X + q.X)) > 0.5f || MathF.Abs(node.Y - (o.Y + q.Y)) > 0.5f
                        || MathF.Abs(node.W - q.W) > 0.5f || MathF.Abs(node.H - q.H) > 0.5f)
                    {
                        faults.Add("Settle " + key + " node " + node + " packed " + q);
                        n++;
                    }
                }
            }
            return n;
        }

        public string? Edit(bool on, string? itemId)
        {
            if (_p!.Board.ReadOnly && on) return "this Home is read-only (a newer Wavee saved it)";
            SetEditing(on, itemId);
            return null;
        }

        public string? Surface(bool open, string kind, string? arg)
        {
            var view = V;
            if (!open) { view.Add.Value = HomeAddSurface.Closed; view.Query.Value = ""; return null; }
            switch (kind)
            {
                case "addrow" or "row":
                    if (!view.Editing.Peek()) SetEditing(true);
                    view.Add.Value = new HomeAddSurface(HomeAddSurfaceKind.Row, arg ?? HomeDocs.Pinned);
                    return null;
                case "find":
                    if (!view.Editing.Peek()) SetEditing(true);
                    view.Add.Value = new HomeAddSurface(HomeAddSurfaceKind.Find);
                    if (arg is not null) view.Query.Value = arg;
                    return null;
            }
            return "classic's add surfaces are addrow (arg = a group id) and find";
        }

        public (IReadOnlyList<HomeCandidate> Hits, string Spoken) Search(string query)
        {
            var view = V;
            if (!view.Editing.Peek()) SetEditing(true);
            ClassicHomeFind.Type(_p!.Board, query);
            var hits = ClassicHomeFind.Results(_p.Board);
            return (hits, ClassicHomeFind.Spoken(_p.Board, query.Trim(), hits.Count));
        }

        public void Pin(string pinId, string name, string cover, string? groupId = null) => Vb.Pin(pinId, name, cover, groupId);
        public void Unpin(string pinId) => Vb.Unpin(pinId);
        public void Add(HomeCandidate candidate, int atEntryIndex = -1, HomeSize size = HomeSize.Auto) => Vb.Add(candidate, atEntryIndex, size);
        public void Remove(string entryId) => Vb.Remove(entryId);
        public void SetSize(string entryId, HomeSize size) => Vb.SetSize(entryId, size);
        public void CycleSize(string entryId) => Vb.CycleSize(entryId);
        public void MoveOnto(string entryId, string targetEntryId) => Vb.MoveOnto(entryId, targetEntryId);
        public void MoveBy(string entryId, int delta) => Vb.MoveBy(entryId, delta);
        public void MoveToEdge(string entryId, bool toStart) => Vb.MoveToEdge(entryId, toStart);

        public void MoveToward(string entryId, int dx, int dy)
        {
            if (dy == 0) { Vb.MoveBy(entryId, dx); return; }
            if (_reorder is not null && _reorder.MoveToward(HomeBoardKeys.Tile(entryId), dx, dy)) return;
            if (_p!.Board.Grid.Neighbour(HomeBoardKeys.Tile(entryId), 0, dy) is { } k && IdOf(k) is { Length: > 0 } target) Vb.MoveOnto(entryId, target);
        }

        public void Open(string entryId) => Vb.Open(entryId);
        public void Close(bool refocus) => Vb.Close(refocus);
        public void SetOption(string key, string value) => Vb.SetOption(key, value);
        public void Undo() => Vb.Undo();
        public void Redo() => Vb.Redo();

        public void Reset(HomeResetScope scope, bool confirm)
        {
            if (scope == HomeResetScope.Page) { Vb.Reset(HomeResetScope.Page); return; }
            if (!V.Editing.Peek()) SetEditing(true);
            if (!confirm) { V.ConfirmingReset.Value = true; return; }
            V.ConfirmingReset.Value = false;
            Vb.Reset(HomeResetScope.Everything);
        }
    }

    sealed record RingProps(HomeBoardView View);

    /// <summary>The ring around a tile just added shows for 1.8 s (`RingMs`), then goes; it draws nothing itself.</summary>
    sealed class RingClear : Component
    {
        public override Element Render()
        {
            var view = UseProps<RingProps>().View;
            string? id = view.RingId.Value;
            UseTimeout(() => { if (id is not null && view.RingId.Peek() == id) view.RingId.Value = null; }, ClassicHomeBoardRules.RingMs,
                DepKey.From(id is null ? 0 : StringComparer.Ordinal.GetHashCode(id)));
            return new BoxEl { Visible = false };
        }
    }

    /// <summary>The group a candidate goes to, by its title ("Add to Now", "Add to Pinned").</summary>
    internal static string GroupTitleOf(HomeBoardProps board, string groupId) => GroupTitle(board.Model.Peek(), groupId);
}
