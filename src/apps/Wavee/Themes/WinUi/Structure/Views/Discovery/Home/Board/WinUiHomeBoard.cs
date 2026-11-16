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

/// <summary>What the WinUI grid packs: a board entry, or the empty Pinned row the theme adds after an empty group head.</summary>
internal sealed record WinUiBoardItem(string Key, BoardEntry Entry, bool EmptyRow);

/// <summary>WinUI's pinned Home board.</summary>
internal static class WinUiHomeBoard
{
    /// <summary>The registered template of <see cref="ViewKeys.HomeBoard"/>.</summary>
    public static Element Template(HomeBoardProps props, in ViewContext ctx)
    {
        var facts = ctx.Tier();
        return Embed.Comp(new BoardProps(props, WinUiHomeBoardTiers.Of(facts), WinUiHomeTiers.Of(facts)), static () => new BoardView())
            with { Key = "home:board-root" };
    }

    internal sealed record BoardProps(HomeBoardProps Board, WinUiHomeBoardLayout Lay, WinUiHomeLayout Home);

    /// <summary>The board's component: its width, the measured full rows, the items memo and the reorder.</summary>
    internal sealed class BoardView : Component
    {
        readonly Signal<float> _width = new(0f);
        readonly Dictionary<string, Signal<float>> _extent = new(StringComparer.Ordinal);
        readonly Action<RectF> _onBounds;
        BoardProps? _p;
        PackReorder<WinUiBoardItem>? _reorder;
        PackGridOptions<WinUiBoardItem>? _options;
        Func<WinUiBoardItem, PackCell, Element>? _template;
        Func<PackBox, HomeTileForm>? _formRule;

        public BoardView()
        {
            _onBounds = r => { if (r.W > 0f && MathF.Abs(r.W - _width.Peek()) > 0.25f) _width.Value = r.W; };
        }

        /// <summary>A full row's measured height (0 until its body has laid out once).</summary>
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
            var items = UseComputed(() => ItemsOf(_p!.Board.Model.Value));

            _formRule ??= box => WinUiHomeBoardRules.FormOf(in box, _p!.Lay, _p.Lay.ColumnsAt(_width.Peek()));
            _template ??= (item, cell) => Item(item, cell);
            _reorder ??= new PackReorder<WinUiBoardItem>("home.tile")
            {
                CanLift = static it => !it.EmptyRow && it.Entry.Kind != BoardEntryKind.GroupHead,
                LiftStyle = new DragVisualStyle { Scale = 1.03f },
                Slot = static rect => WinUiHomeTiles.Slot(rect),
                AcceptKinds = [WinUiHomeAddBand.DragKind],
                CanAccept = static payload => payload is HomeCandidate,
                OnCommit = m => Commit(m),
                OnForeignCommit = (payload, index) => ForeignCommit(payload, index),
                AnnounceText = a => Announce(a),
            };
            _options ??= new PackGridOptions<WinUiBoardItem>
            {
                KeyOf = static it => it.Key,
                EntryOf = it => EntryOf(it),
                Metrics = () => _p!.Lay.Metrics(_width.Value),
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
                OnBoundsChanged = _onBounds,
                OnKeyDown = OnKey,
                Children =
                [
                    WinUiHomeAddBand.Disclosure(board, p.Lay, bandOpen),
                    PackGrid.Create(items, _template, _options),
                ],
            };
        }

        static IReadOnlyList<WinUiBoardItem> ItemsOf(BoardModel m)
        {
            var e = m.Entries;
            var list = new List<WinUiBoardItem>(e.Length + 1);
            for (int i = 0; i < e.Length; i++)
            {
                var x = e[i];
                list.Add(new WinUiBoardItem(x.Kind == BoardEntryKind.GroupHead ? HomeBoardKeys.Group(x.GroupId) : HomeBoardKeys.Tile(x.Id), x, false));
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
            if (e.Kind == BoardEntryKind.GroupHead) return WinUiHomeBoardRules.EntryOf(e, lay, 0f, 0f, false);
            float open = _p.Board.View.OpenId.Value == e.Id ? WinUiHomeBoardRules.OpenExtent(e, e.Source, lay) : 0f;
            float full = 0f;
            if (WinUiHomeBoardRules.SizeOf(e) == HomeSize.Full)
            {
                full = ExtentOf(e.Id).Value;
                if (full <= 0f) full = FullEstimate(e, _p.Home);
            }
            return WinUiHomeBoardRules.EntryOf(e, lay, full, open, false);
        }

        /// <summary>A full row's height before its body has laid out: the Home tier record's own numbers for the bodies it knows.</summary>
        static float FullEstimate(BoardEntry e, WinUiHomeLayout home)
        {
            float head = WinUiHomeRules.TitleLine + (home.HeaderSubtitles ? WinUiHomeRules.SubtitleLine : 0f) + home.HeaderGap;
            if (e.Raw is WidgetSource w)
                switch (w.Widget)
                {
                    case "daylist": return home.Daylist.Cover + 2f * home.Daylist.Pad;
                    case "recents": return head + home.RecentsRows * home.RecentsRowH + (home.RecentsRows - 1) * home.RecentsGap;
                    case "releases": return head + 4f * WinUiHomeRules.ReleaseRowH;
                }
            return WinUiHomeBoardRules.FullFallback;
        }

        Element Item(WinUiBoardItem it, PackCell cell)
        {
            var board = _p!.Board;
            if (it.EmptyRow) return WinUiHomeTiles.EmptyPinned(_p.Lay);
            var e = it.Entry;
            if (e.Kind == BoardEntryKind.GroupHead) return WinUiHomeTiles.GroupHead(e, _p.Lay);
            if (board.View.OpenId.Value == e.Id)
                return WinUiHomeTiles.Open(e, board.Options(e));
            var tile = board.Tile(e, cell, _formRule!);
            if (WinUiHomeBoardRules.SizeOf(e) != HomeSize.Full) return tile;
            // A full row: the body lays out at its own height inside the packed box, and that height becomes the box (by the re-flow).
            var extent = ExtentOf(e.Id);
            return new BoxEl
            {
                Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 1, MinWidth = 0f, Grow = 0f, Shrink = 0f, AlignItems = FlexAlign.Stretch,
                        OnBoundsChanged = r => { if (r.H > 0f && MathF.Abs(r.H - extent.Peek()) > 0.5f) extent.Value = r.H; },
                        Children = [tile],
                    },
                ],
            };
        }

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
                PackAnnounceKind.Move => board.Verbs.Where(id),
                PackAnnounceKind.Drop => Strings.Theme.Winui.Home.Board.Dropped(name: name, where: board.Verbs.Where(id)),
                PackAnnounceKind.Cancel => Strings.Theme.Winui.Home.Board.Cancelled(name: name),
                _ => null,
            };
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

        internal static string IdOf(string key) => key.StartsWith("home:tile:", StringComparison.Ordinal) ? key["home:tile:".Length..] : "";

        /// <summary>The board's keys while focus is inside it (guarded by the text-editor test): Ctrl+Z undoes; "/" opens the add band's search;
        /// Escape unwinds (the open tile, then the band).</summary>
        void OnKey(KeyEventArgs e)
        {
            if (e.Handled || _p is null) return;
            var board = _p.Board;
            if (e.Ctrl && !e.Shift && !e.Alt && e.KeyCode == Keys.Z) { board.Verbs.Undo(); e.Handled = true; return; }
            if (WinUiHomeAddBand.EditorFocused()) return;
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
    }
}
