// ── Themes/Zune/Structure/Views/Home/Board/ZuneHomeBoard.cs — Zune's `Home.Board` (pinned-home-tech.md 1.7; spec 8.2) ─────────────────────
//
// Windows Phone Start, kept on Home. The page gets a TITLE ("good evening", the hero rung, lowercase, wrapping where it must), and under it either the
// board or, as the next section of the panorama, ALL ITEMS: the board slides away to the left and the list slides in under the same title.
//
//   Home.Board
//   └─ column  `home:board-col`  (keys: Ctrl+Z, "/", Delete, arrows while customizing, Enter on a tile, Escape unwinds)
//      ├─ the title row   `home:title`: the page title, the undo word when the last change was page-wide, the "edit" word at the line's end
//      └─ ONE section, keyed: the board (`home:board-section`) or all items (`home:add`, `ZuneHomeAllItems`)
//         board: PackGrid `home:board` (group heads: 40/48 light lowercase titles on ONE line that bleed under a fade, each with its undo word; the
//                empty Pinned row; one wrapper `home:tile:<id>` per entry), then the round arrow to all items, then the app bar's reserve
//
// CUSTOMIZE (`View.Editing` + `View.SelectedId`): entered by the "edit" word at the end of the title's line (always there; it says "done" while
// customizing: owner, 2026-11-16, no toolbar at rest), a held press (600 ms mouse, 500 ms touch), a right-click or Enter
// on a tile. The selected tile comes forward, the others fall back to 94 % and dim (`ZuneHomeTiles`); its badges unpin and cycle its size; a tap on
// another tile selects it; arrows move the selected tile, Delete unpins it, Escape (or "done", or a tap away from the tiles) leaves. Only the selected
// tile drags (`PackReorder.CanLift`). OPTIONS turn the selected tile over (`TileMotion.TurnOver`): its back lists the choices as words
// (`ZuneHomeTileBack`), applied at once; the visit is one undo step (the core's batch).
//
// THE BOX IS THE PACK'S: a band's height is its body's (packed at the rules' estimate, then at the body's measured height, followed by the grid's
// 250 ms re-flow); every other tile is f(size class, columns, the group's fill). Nothing a tile loads moves anything.

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
using Wavee.Theming.Roles;
using B = Wavee.Themes.Zune.ZuneHomeBoardRules;

namespace Wavee.Themes.Zune;

/// <summary>What the Zune grid packs: a board entry, or the empty Pinned row the theme adds after an empty group head.</summary>
/// <para><see cref="Switch"/> says the Pinned group carries the history tile, so its head is the two pivot words (<see cref="HomeShortcuts"/>).</para>
internal sealed record ZuneBoardItem(string Key, BoardEntry Entry, bool EmptyRow, bool Switch = false);

/// <summary>Zune's pinned Home board.</summary>
internal static class ZuneHomeBoard
{
    /// <summary>The registered template of <see cref="ViewKeys.HomeBoard"/>.</summary>
    public static Element Template(HomeBoardProps props, in ViewContext ctx)
    {
        var facts = ctx.Tier();
        return Embed.Comp(new BoardProps(props, ZuneHomeBoardTiers.Of(facts), ZuneHomeTiers.Of(facts)), static () => new BoardView())
            with { Key = "home:board-root" };
    }

    internal sealed record BoardProps(HomeBoardProps Board, ZuneHomeBoardLayout Lay, ZuneHomeLayout Home);

    /// <summary>The entry id of a tile key ("" for anything else).</summary>
    internal static string IdOf(string key) => key.StartsWith("home:tile:", StringComparison.Ordinal) ? key["home:tile:".Length..] : "";

    // ══ the customize state (shared by the tiles, the bar and the keys) ═════════════════════════════════════════════════

    /// <summary>Enter customize with <paramref name="id"/> selected (the first tile when null).</summary>
    internal static void Customize(HomeBoardView view, IHomeBoardVerbs verbs, BoardModel model, string? id)
    {
        id ??= FirstTile(model);
        bool was = view.Editing.Peek();
        view.Editing.Value = true;
        if (id is null) return;
        Select(view, verbs, model, id, speak: was);
        if (!was) verbs.Say(Strings.Theme.Zune.Home.Customizing(name: NameOf(model, id)));
    }

    /// <summary>Select <paramref name="id"/> (customizing): it comes forward, the others recede; an open tile turns back.</summary>
    internal static void Select(HomeBoardView view, IHomeBoardVerbs verbs, BoardModel model, string id, bool speak = true)
    {
        if (view.OpenId.Peek() is { } open && open != id) verbs.Close(refocus: false);
        if (view.SelectedId.Peek() == id) return;
        view.SelectedId.Value = id;
        view.Grid?.Focus(HomeBoardKeys.Tile(id));
        if (speak) verbs.Say(Strings.Theme.Zune.Home.Selected(name: NameOf(model, id)));
    }

    /// <summary>Leave customize ("done", Escape, a tap away): the tiles come back, the bar goes back to customize.</summary>
    internal static void Done(HomeBoardView view, IHomeBoardVerbs verbs)
    {
        if (!view.Editing.Peek()) return;
        if (view.OpenId.Peek() is not null) verbs.Close(refocus: false);
        // Focus stays where it is: moving it to the tile would bring the tile into view, a scroll jump when "done" is pressed far from it.
        view.Editing.Value = false;
        view.SelectedId.Value = null;
        verbs.Say(Loc.Get(Strings.Theme.Zune.Home.DoneSaid));
    }

    /// <summary>"options": turn the selected tile over, or back.</summary>
    internal static void ToggleOptions(HomeBoardView view, IHomeBoardVerbs verbs)
    {
        if (view.SelectedId.Peek() is not { } id) return;
        if (view.OpenId.Peek() == id) verbs.Close(refocus: true);
        else verbs.Open(id);
    }

    /// <summary>The selected entry can turn over (it declares options or is live).</summary>
    internal static bool CanTurn(BoardModel model, string? id)
    {
        if (id is null) return false;
        int i = model.IndexOf(id);
        return i >= 0 && model.Entries[i] is { } e && (e.HasOptions || e.Live) && e.Source is { Options.Length: > 0 };
    }

    /// <summary>Unpin (or hide) the selected tile and select the next one.</summary>
    internal static void RemoveSelected(HomeBoardView view, IHomeBoardVerbs verbs, BoardModel model)
    {
        if (view.SelectedId.Peek() is not { } id) return;
        string? next = Neighbour(model, id);
        verbs.Remove(id);
        view.SelectedId.Value = next;
        if (next is not null) view.Grid?.Focus(HomeBoardKeys.Tile(next));
    }

    /// <summary>The tile after <paramref name="id"/> in reading order (else the one before), or null.</summary>
    internal static string? Neighbour(BoardModel model, string id)
    {
        var e = model.Entries;
        int at = model.IndexOf(id);
        if (at < 0) return null;
        for (int i = at + 1; i < e.Length; i++) if (IsTile(e[i])) return e[i].Id;
        for (int i = at - 1; i >= 0; i--) if (IsTile(e[i])) return e[i].Id;
        return null;
    }

    /// <summary>The tile <paramref name="step"/> places from <paramref name="id"/> in reading order, or null.</summary>
    internal static string? Step(BoardModel model, string id, int step)
    {
        var e = model.Entries;
        int at = model.IndexOf(id);
        if (at < 0) return null;
        for (int i = at + step; i >= 0 && i < e.Length; i += step) if (IsTile(e[i])) return e[i].Id;
        return null;
    }

    static bool IsTile(BoardEntry e) => e.Kind != BoardEntryKind.GroupHead && !e.Hidden;

    static string? FirstTile(BoardModel model)
    {
        foreach (var e in model.Entries) if (IsTile(e)) return e.Id;
        return null;
    }

    internal static string NameOf(BoardModel model, string id)
    {
        int i = model.IndexOf(id);
        return i >= 0 ? model.Entries[i].Title : "";
    }

    /// <summary>The title an undo belongs by: the group the last change was in while that group shows, else the page title ("page").</summary>
    internal static string UndoAt(HomeBoardProps board)
    {
        var u = board.Undo.Value;
        if (board.View.Add.Value.Kind == HomeAddSurfaceKind.AllItems || u.At is null or "page") return "page";
        foreach (var e in board.Model.Value.Entries)
            if (e.Kind == BoardEntryKind.GroupHead && e.GroupId == u.At) return u.At;
        return "page";
    }

    // ══ the board's component ═════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The board: its width, the measured bands, the items memo, the reorder, the turned tiles, the keys.</summary>
    internal sealed class BoardView : Component
    {
        readonly Signal<float> _width = new(0f);
        readonly Dictionary<string, Signal<float>> _extent = new(StringComparer.Ordinal);
        readonly Dictionary<string, Signal<bool>> _turned = new(StringComparer.Ordinal);
        readonly Signal<bool> _titleHover = new(false);
        readonly Action<RectF> _onBounds;
        readonly Action _reveal;
        BoardProps? _p;
        PackReorder<ZuneBoardItem>? _reorder;
        PackGridOptions<ZuneBoardItem>? _resting, _customizing;
        Func<ZuneBoardItem, PackCell, Element>? _template;
        Func<PackBox, HomeTileForm>? _formRule, _pinFormRule;
        TimerHandle _revealTimer;
        bool _rendered, _wasAll;

        static readonly Func<PackBox, HomeTileForm> s_full = static _ => HomeTileForm.Full;
        static readonly Func<PackBox, HomeTileForm> s_medium = static _ => HomeTileForm.M;

        public BoardView()
        {
            _onBounds = r => { if (r.W > 0f && MathF.Abs(r.W - _width.Peek()) > 0.25f) _width.Value = r.W; };
            _reveal = () => { if (_p?.Board.View.RingId.Peek() is { } id) _p.Board.Grid.BringIntoView(HomeBoardKeys.Tile(id)); };
        }

        // A measured body lands on its box at the top of the NEXT frame (Measured): the last height a frame laid it out at is the one it
        // presents. A body whose height eases (RowsResize) is first laid out at its new height and only then put back at the ease's start, so
        // a height written at once would pack every row under it at the far height for one frame and then pull them back.
        readonly Dictionary<string, float> _measured = new(StringComparer.Ordinal);
        // The rows whose box follows their body: a body's FIRST height re-packs the board with its re-flow (the estimate it mounted at was
        // the rules' guess); every later one is the body's own (eased) height, which the box follows frame by frame.
        readonly HashSet<string> _follows = new(StringComparer.Ordinal);
        Action<Action>? _post;
        Action? _applyMeasured;
        bool _measurePosted;

        void Measured(Signal<float> extent, string id, float h)
        {
            if (h <= 0f) return;
            if (extent.Peek() <= 0f) { if (MathF.Abs(h - extent.Peek()) > 0.5f) extent.Value = h; return; }   // the first measure: at once
            _measured[id] = h;
            if (_measurePosted || _post is null) return;
            _measurePosted = true;
            _post(_applyMeasured ??= ApplyMeasured);
        }

        void ApplyMeasured()
        {
            _measurePosted = false;
            foreach (var (id, h) in _measured)
            {
                var s = ExtentOf(id);
                if (MathF.Abs(h - s.Peek()) > 0.5f) { s.Value = h; _follows.Add(id); }
            }
            _measured.Clear();
        }

        Signal<float> ExtentOf(string id)
        {
            if (!_extent.TryGetValue(id, out var s)) _extent[id] = s = new Signal<float>(0f);
            return s;
        }

        Signal<bool> TurnedOf(string id)
        {
            if (!_turned.TryGetValue(id, out var s)) _turned[id] = s = new Signal<bool>(_p?.Board.View.OpenId.Peek() == id);
            return s;
        }

        public override Element Render()
        {
            var p = UseProps<BoardProps>();
            _p = p;
            _post = UsePost();
            var board = p.Board;
            var view = board.View;
            view.NoEditMode = null;
            var model = board.Model;
            ZuneHomeTiles.Shown = () => model.Peek();
            var items = UseComputed(() => ItemsOf(_p!.Board.Model.Value));
            bool editing = view.Editing.Value;
            bool all = view.Add.Value.Kind == HomeAddSurfaceKind.AllItems;
            string? open = view.OpenId.Value;
            _revealTimer = UseTimeout(_reveal, B.RecedeMs + 50f);

            // The turned tiles follow the open id (a signal per tile, so only the tile that turns re-renders).
            UseLayoutEffect(() =>
            {
                foreach (var kv in _turned) { bool t = kv.Key == open; if (kv.Value.Peek() != t) kv.Value.Value = t; }
            }, DepKey.From(open?.GetHashCode() ?? 0));

            // Customize always has a selected tile (the first, when it was entered with none: the inspector's `home do=edit`, a mode set elsewhere).
            UseLayoutEffect(() =>
            {
                if (view.Editing.Peek() && view.SelectedId.Peek() is null && FirstTile(board.Model.Peek()) is { } first) view.SelectedId.Value = first;
            }, DepKey.From(editing ? 1 : 0));

            // Back from all items: the tile a tap pinned (the ring) is brought into view once the board has slid back in and packed.
            bool back = _wasAll && !all;
            _wasAll = all;
            UseLayoutEffect(() => { if (back && view.RingId.Peek() is not null) _revealTimer.RestartIn(B.RecedeMs + 50f); }, DepKey.From(back ? 1 : 0));

            _formRule ??= box => ZuneHomeBoardRules.FormOf(in box, _p!.Lay);
            _pinFormRule ??= box => ZuneHomeBoardRules.PinFormOf(in box, _p!.Lay);
            _template ??= (item, cell) => Item(item, cell);
            _reorder ??= new PackReorder<ZuneBoardItem>("home.tile")
            {
                // Only the selected tile drags, and only while customizing (Start's: the selected tile moves, the others flow round it).
                Enabled = () => _p!.Board.View.Editing.Value,
                // The shortcuts grid is the Pinned group's whole body: its cells move inside it, the grid itself stays.
                CanLift = it => !it.EmptyRow && it.Entry.Kind != BoardEntryKind.GroupHead && !HomeShortcuts.IsGrid(it.Entry)
                                && _p!.Board.View.SelectedId.Value == it.Entry.Id,
                LiftStyle = new DragVisualStyle { Scale = 1.03f },
                Slot = static rect => ZuneHomeTiles.Slot(rect),
                OnCommit = m => Commit(m),
                AnnounceText = a => Announce(a),
            };
            _resting ??= new PackGridOptions<ZuneBoardItem>
            {
                KeyOf = static it => it.Key,
                EntryOf = it => EntryOf(it),
                Metrics = () => _p!.Lay.Metrics(_width.Value),
                Controller = board.Grid,
                Scroll = board.Scroll,
                Reorder = _reorder,
                Automation = it => Automation(it),
                // A band's heading sits on the page gutter, the tile's own left edge: a glyph that hangs left of its box ("j") keeps its ink.
                InkOutset = it => !it.EmptyRow && it.Entry.Kind != BoardEntryKind.GroupHead && ZuneHomeBoardRules.SizeOf(it.Entry) == HomeSize.Full
                    ? B.BandInkOutset : default,
                Key = HomeBoardKeys.Board,
            };
            // While customizing the arrows move the selected tile (the board's keys), so the grid leaves them alone.
            _customizing ??= _resting with { ArrowNavigation = false };

            bool rendered = _rendered;
            _rendered = true;
            Element section = all
                ? ZuneHomeAllItems.Section(board, p.Lay, rendered)
                : BoardSection(board, p.Lay, PackGrid.Create(items, _template, editing ? _customizing : _resting), editing, rendered);

            return new BoxEl
            {
                Key = "home:board-col", Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
                OnBoundsChanged = _onBounds,
                OnKeyDown = OnKey,
                Children = [TitleRow(board, p.Lay, editing, all), section],
            };
        }

        /// <summary>The page title, its undo word, and at the line's end the "edit" word (owner, 2026-11-16: the edit toolbar shows only while
        /// customizing; the way in is a quiet word that is always there). Customizing, the word says "done". One line where it fits, the title
        /// WRAPPING where it does not (never smaller); the word keeps its place at the end of the title's last line.</summary>
        Element TitleRow(HomeBoardProps board, ZuneHomeBoardLayout lay, bool editing, bool all) => new BoxEl
        {
            Key = B.TitleKey, Direction = 0, MinWidth = 0f, AlignItems = FlexAlign.End, Gap = B.UndoGap,
            Padding = new Edges4(0f, 0f, 0f, lay.TitleGap),
            OnHoverMove = _ => { if (!_titleHover.Peek()) _titleHover.Value = true; },
            OnPointerExit = () => { if (_titleHover.Peek()) _titleHover.Value = false; },
            Children =
            [
                R.Type.Hero("") with
                {
                    Key = B.TitleTextKey, Text = Prop.Of(static () => DaylistCard.PartOfDay()), Case = R.Case.For(LabelKind.Heading),
                    Wrap = TextWrap.Wrap, Shrink = 1f, MinWidth = 0f,
                },
                ZuneHomeUndoWord.Word(board, "page", _titleHover),
                new BoxEl { Grow = 1f, MinWidth = 0f },
                EditWord(board, editing, all),
            ],
        };

        /// <summary>The "edit" word: the command voice (15/20, lowercase), the secondary ink, the accent on hover, a 40 target; "done" while customizing.
        /// While all items shows it stands aside (its slot is kept: nothing moves).</summary>
        static Element EditWord(HomeBoardProps board, bool editing, bool all)
        {
            var view = board.View;
            var verbs = board.Verbs;
            return new BoxEl
            {
                Key = B.EditWordKey, Direction = 0, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.End, AlignSelf = FlexAlign.End,
                MinWidth = B.BadgeHit, MinHeight = B.BadgeHit, Padding = new Edges4(B.UndoPadX, 0f, 0f, 0f),
                Focusable = !all, HitTestVisible = !all, Opacity = all ? 0f : 1f, Role = AutomationRole.Button, Cursor = CursorId.Hand,
                AllowFocusOnInteraction = false,
                OnClick = () =>
                {
                    if (view.Editing.Peek()) Done(view, verbs);
                    else Customize(view, verbs, board.Model.Peek(), null);
                },
                Children =
                [
                    R.Type.CardTitle(Loc.Get(editing ? Strings.Theme.Zune.Home.Done : Strings.Theme.Zune.Home.Edit)) with
                    {
                        Color = Tok.TextSecondary, HoverColor = Tok.AccentTextPrimary, Case = R.Case.For(LabelKind.Command), MaxLines = 1, Wrap = TextWrap.NoWrap,
                    },
                ],
            };
        }

        /// <summary>The board section: the grid, the round arrow to all items, the app bar's reserve. A tap on it (not on a tile) is "done".</summary>
        Element BoardSection(HomeBoardProps board, ZuneHomeBoardLayout lay, Element grid, bool editing, bool rendered)
        {
            float slide = Design.Reduced ? 0f : -B.SectionSlide;
            var view = board.View;
            var verbs = board.Verbs;
            return new BoxEl
            {
                Key = "home:board-section", Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
                Enter = rendered ? new EnterExit(Opacity: 0f, Dx: slide, Active: true) : null,
                Exit = new EnterExit(Opacity: 0f, Dx: slide, Active: true),
                Transition = MotionTokenDef.Eased(B.RecedeMs, Easing.FluentDecelerate, ReducedMotionPolicy.KeepFade),
                OnClick = editing ? () => Done(view, verbs) : null,
                Children =
                [
                    // The pack's height changes with a re-pack (a crossing's new columns, a resize, a tile turned over): its presented height eases on the
                    // chapters' clock, so what stands under the board (the round arrow, the foot) rides it instead of jumping by the difference.
                    new BoxEl
                    {
                        Key = "home:board-pack", Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
                        Layout = Design.Reduced ? null : ZuneHomeRules.ChapterResize, Children = [grid],
                    },
                    ZuneHomeAllItems.EndRow(board, lay),
                    new BoxEl { Height = lay.FootReserve, Shrink = 0f, HitTestVisible = false },
                ],
            };
        }

        static IReadOnlyList<ZuneBoardItem> ItemsOf(BoardModel m)
        {
            var e = m.Entries;
            var list = new List<ZuneBoardItem>(e.Length + 1);
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
                list.Add(new ZuneBoardItem(x.Kind == BoardEntryKind.GroupHead ? HomeBoardKeys.Group(x.GroupId) : HomeBoardKeys.Tile(x.Id), x, false,
                                           sw && x.GroupId == HomeDocs.Pinned));
                // An empty Pinned group says how to pin (spec 8.2 "Empty states"): large light words at the group's height.
                if (x.Kind == BoardEntryKind.GroupHead && x.GroupId == "pinned" && (i + 1 >= e.Length || e[i + 1].Kind == BoardEntryKind.GroupHead))
                    list.Add(new ZuneBoardItem(B.EmptyKey, x, true));
            }
            return list;
        }

        PackEntry EntryOf(ZuneBoardItem it)
        {
            var lay = _p!.Lay;
            if (it.EmptyRow) return new PackEntry(PackEntryKind.Row, Extent: lay.EmptyRow);
            var e = it.Entry;
            if (e.Kind == BoardEntryKind.GroupHead) return ZuneHomeBoardRules.EntryOf(e, lay, 0f, 0f);
            // The shortcuts grid: a band at its own height (its tallest page).
            if (HomeShortcuts.IsGrid(e))
            {
                float grid = ExtentOf(e.Id).Value;
                return grid > 0f
                    ? new PackEntry(PackEntryKind.Row, Flags: _follows.Contains(e.Id) ? PackFlags.Follows : PackFlags.None, Extent: grid)
                    : new PackEntry(PackEntryKind.Row, Extent: ShortcutGrid.Extent(_p.Board.Model.Value, Shortcuts(), _width.Value));
            }
            float open = _p.Board.View.OpenId.Value == e.Id ? ZuneHomeBoardRules.OpenExtent(e.Source) : 0f;
            float full = 0f;
            bool measured = false;
            if (ZuneHomeBoardRules.SizeOf(e) == HomeSize.Full)
            {
                full = ExtentOf(e.Id).Value;
                measured = full > 0f;
                if (!measured) full = FullEstimate(e, _p.Home);
            }
            var entry = ZuneHomeBoardRules.EntryOf(e, lay, full, open);
            // A measured band's extent is its body's height, which eases itself (RowsResize): the bands follow it frame by frame.
            return measured && _follows.Contains(e.Id) && open <= 0f && entry.Kind == PackEntryKind.Band ? entry with { Flags = entry.Flags | PackFlags.Follows } : entry;
        }

        /// <summary>A band's height before its body has laid out: the Home tier record's own numbers for the bodies it knows.</summary>
        static float FullEstimate(BoardEntry e, ZuneHomeLayout home)
        {
            float head = ZuneHomeRules.HeadGap + 32f;
            if (e.Raw is WidgetSource w)
                switch (w.Widget)
                {
                    case "daylist": return home.DaylistH;
                    case "releases": return head + 4f * home.ReleaseRowH + 3f * ZuneHomeRules.RowGap;
                    case "recents": return head + home.QuickTileW + 2f * ZuneHomeRules.TileCaptionGap + 36f;
                }
            return B.FullFallback;
        }

        Element Item(ZuneBoardItem it, PackCell cell)
        {
            var board = _p!.Board;
            if (it.EmptyRow) return ZuneHomeTiles.EmptyPinned();
            var e = it.Entry;
            if (e.Kind == BoardEntryKind.GroupHead) return ZuneHomeTiles.GroupHead(board, e, _p.Lay, it.Switch);
            bool grid = HomeShortcuts.IsGrid(e);
            if (!grid && ZuneHomeBoardRules.SizeOf(e) != HomeSize.Full)
            {
                // Every tile can turn over: the front is the tile its box carries, the back its options (only the open tile builds it).
                var rule = e.Raw is PinSource ? _pinFormRule! : _formRule!;
                if (e.Source is not { Options.Length: > 0 }) return board.Tile(e, cell, rule);
                var entry = e;
                return TileMotion.TurnOver(TurnedOf(e.Id), () => board.Tile(entry, cell, rule), () => board.Options(entry));
            }
            // A band: its body lays out at its own height inside the packed box, and that height becomes the box (by the re-flow).
            var extent = ExtentOf(e.Id);
            string id = e.Id;
            var tile = grid ? ShortcutGrid.Create(board, Shortcuts(), _width.Peek()) : board.Tile(e, cell, s_full);
            return new BoxEl
            {
                Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 1, MinWidth = 0f, Grow = 0f, Shrink = 0f, AlignItems = FlexAlign.Stretch,
                        // A body whose rows change with the width (a grid gaining or losing a column, a shelf's tiles stepping a size, the shortcuts'
                        // page) eases to its new height through real layout, so the box measured here, the packed band and every band under it ride
                        // the ease instead of stepping by the difference on the frame the columns change.
                        Layout = ZuneMotion.RowsResize,
                        OnBoundsChanged = r => Measured(extent, id, r.H),
                        Children = [tile],
                    },
                ],
            };
        }

        ShortcutGridSpec? _shortcuts;
        ZuneHomeBoardLayout? _shortcutsLay;

        /// <summary>Zune's shortcuts grid: Start's square tiles, the tier's count across (each at least its floor), 8 apart; each tile the pin's own
        /// medium face (art with the name over a scrim), the history item at its place while the head shows history; in customize only the
        /// selected tile drags (Start's); the strip's edge chevrons turn the pages, the head counts them.</summary>
        ShortcutGridSpec Shortcuts()
        {
            var p = _p!;
            if (_shortcuts is { } s && ReferenceEquals(_shortcutsLay, p.Lay)) return s;
            _shortcutsLay = p.Lay;
            var lay = p.Lay;
            var board = p.Board;
            return _shortcuts = new ShortcutGridSpec(
                Columns: w => Math.Clamp((int)MathF.Floor((w + lay.Gap) / (lay.ShortcutMinCell + lay.Gap)), 1, lay.ShortcutMaxCols),
                Unit: static cellW => cellW,
                Gap: lay.Gap,
                Cell: (e, index, cell) =>
                {
                    // A shortcut is Start's medium face at every tile (the art, the name over a scrim at its foot), the history tile its own.
                    var rule = HomeShortcuts.IsHistoryCell(e) ? _formRule! : s_medium;
                    return HomeShortcuts.HasSwitch(board.Model.Peek())
                        ? ZuneHomeTiles.ShortcutCell(board.Tile(e, cell, rule), index, board, cell, _p!.Lay)
                        : board.Tile(e, cell, rule);
                },
                CanReorder: () => board.View.Editing.Value && !board.ReadOnly,   // a subscribing read: the page grids wire their lift when Edit Home starts
                // Zune's shelf chevrons (the circle in its 40 target) at the pages' edges, on hover.
                Edge: static (prev, pager) => IconButton.Create(prev ? Icons.ChevronLeft : Icons.ChevronRight, prev ? pager.Prev : pager.Next,
                                                                 parts: ZuneDiscoveryKit.ChevronTargets)
                    with { Key = prev ? "home:shortcuts-edge:prev" : "home:shortcuts-edge:next" },
                // Start's: only the selected tile drags (a subscribing read: the page grids re-wire their lift when the selection moves).
                CanLift: e => board.View.SelectedId.Value == e.Id);
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

        string? Announce(PackAnnounce a)
        {
            var board = _p!.Board;
            string id = IdOf(a.Key);
            return a.Kind switch
            {
                PackAnnounceKind.Move or PackAnnounceKind.Drop => board.Verbs.Where(id),
                PackAnnounceKind.Grab => NameOf(board.Model.Peek(), id),
                _ => null,
            };
        }

        PackAutomation Automation(ZuneBoardItem it)
        {
            var e = it.Entry;
            return new PackAutomation(it.EmptyRow ? Loc.Get(Strings.Theme.Zune.Home.EmptyTitle) : e.Title,
                                      e.Kind == BoardEntryKind.GroupHead || it.EmptyRow ? AutomationRole.Text : AutomationRole.Button, 0, 0);
        }

        /// <summary>The board's keys while focus is inside it (single keys stand aside for a text editor).</summary>
        void OnKey(KeyEventArgs e)
        {
            if (e.Handled || _p is null) return;
            var board = _p.Board;
            var view = board.View;
            var verbs = board.Verbs;
            var model = board.Model.Peek();
            bool editor = board.EditorFocused();
            if (!editor && e.Ctrl && !e.Shift && !e.Alt && e.KeyCode == Keys.Z) { verbs.Undo(); e.Handled = true; return; }
            if (e.KeyCode == Keys.Escape)
            {
                if (ZuneHomeAllItems.Escape(board)) { e.Handled = true; return; }
                if (view.OpenId.Peek() is not null) { verbs.Close(refocus: true); e.Handled = true; return; }
                if (view.Editing.Peek()) { Done(view, verbs); e.Handled = true; }
                return;
            }
            if (editor) return;
            if (!e.Ctrl && !e.Alt && e.KeyCode == B.SlashKey) { ZuneHomeAllItems.OpenSearch(board); e.Handled = true; return; }
            if (view.Add.Peek().Kind == HomeAddSurfaceKind.AllItems) return;
            string focused = board.Grid.FocusedKey is { } fk ? IdOf(fk) : "";
            if (!view.Editing.Peek())
            {
                // Enter on a focused tile: customize with it selected (spec 8.2: "focus a tile, Enter").
                if (e.KeyCode == Keys.Enter && e.Mods == KeyModifiers.None && focused.Length > 0)
                {
                    Customize(view, verbs, model, focused);
                    e.Handled = true;
                }
                return;
            }
            if (view.SelectedId.Peek() is not { } sel) return;
            if (focused.Length > 0 && focused != sel) { Select(view, verbs, model, focused); sel = focused; }
            // Shift+Tab on the selected tile selects the one before (Tab walks into its badges; the resize badge's Tab selects the next).
            if (e.KeyCode == Keys.Tab && e.Shift && !e.Ctrl && !e.Alt && focused == sel)
            {
                if (Step(model, sel, -1) is { } prev) { Select(view, verbs, model, prev); e.Handled = true; }
                return;
            }
            if (e.Mods != KeyModifiers.None) return;
            switch (e.KeyCode)
            {
                case Keys.Left: case Keys.Right:
                    verbs.MoveBy(sel, e.KeyCode == Keys.Left ? -1 : 1);
                    board.Grid.Focus(HomeBoardKeys.Tile(sel));
                    e.Handled = true;
                    return;
                case Keys.Up: case Keys.Down:
                    if (board.Grid.Neighbour(HomeBoardKeys.Tile(sel), 0, e.KeyCode == Keys.Up ? -1 : 1) is { } nk && IdOf(nk) is { Length: > 0 } target)
                        verbs.MoveOnto(sel, target);
                    board.Grid.Focus(HomeBoardKeys.Tile(sel));
                    e.Handled = true;
                    return;
                case Keys.Delete:
                    RemoveSelected(view, verbs, model);
                    e.Handled = true;
                    return;
                case Keys.Enter:
                    if (CanTurn(model, sel)) { ToggleOptions(view, verbs); e.Handled = true; }
                    return;
            }
        }
    }
}
