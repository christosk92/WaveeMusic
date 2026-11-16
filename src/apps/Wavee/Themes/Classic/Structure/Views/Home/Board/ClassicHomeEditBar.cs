// ── Themes/Classic/Structure/Views/Home/Board/ClassicHomeEditBar.cs — Classic's `Home.BoardTools`: Home's ⋯, and Edit Home's bar in its slot (spec 8.3) ──
//
// At rest the facet row ends with Home's ⋯ ("Home options": Edit Home, Show on Home ›). In Edit Home the SAME slot holds the sidebar's edit bar, built
// from its own parts (`ClassicEditBarParts`): the pencil, "Edit Home", "Find something to add", Undo, Redo, "Reset ▾", the accent "Done". The facet
// band is the page's pinned band (`ClassicHomeFacetRow`, sticky against the page scope), so the bar stays put while the page scrolls with no sticky code
// of its own. Where the row is too narrow for the field (Compact) the bar keeps its buttons and the field takes its own line under the band, in the
// board's find strip (`ClassicHomeFind`): "under 520 the bar wraps and the field takes the width".
//
// "Reset ▾" is a menu: "Reset this page" acts at once (a toast with Undo); "Reset everything…" ASKS IN THE BAR ITSELF, the sidebar's question and its
// sentence in the bar's own row, [Reset everything] [Cancel] (no dialog: owner's decision, spec 8.5). Esc cancels it.

using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Animation;
using FluentGpu.Forms;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Input;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>Classic's board tools in the facet row's end.</summary>
internal static class ClassicHomeEditBar
{
    static readonly Signal<int> s_focusMore = new(0);
    static readonly Signal<int> s_focusFind = new(0);

    /// <summary>Return focus to Home's ⋯ (Edit Home's Done, Esc).</summary>
    public static void FocusMore() => s_focusMore.Value = s_focusMore.Peek() + 1;

    /// <summary>Put focus in "Find something to add" ("/" in Edit Home).</summary>
    public static void FocusFind() => s_focusFind.Value = s_focusFind.Peek() + 1;

    /// <summary>A pending ask to focus the find field (the strip's field reads it too at Compact).</summary>
    internal static IReadSignal<int> FindAsk => s_focusFind;

    /// <summary>The registered template of <see cref="ViewKeys.HomeBoardTools"/>.</summary>
    public static Element Template(HomeBoardProps props, in ViewContext ctx)
        => Embed.Comp(new ToolsProps(props, ClassicHomeBoardTiers.Of(ctx.Tier())), static () => new Tools()) with { Key = "home:tools" };

    sealed record ToolsProps(HomeBoardProps Board, ClassicHomeBoardLayout Lay);

    sealed class Tools : Component
    {
        NodeHandle _more, _reset, _done;
        OverlayHandle? _menu;
        ToolsProps? _p;

        public override Element Render()
        {
            var p = UseProps<ToolsProps>();
            _p = p;
            var board = p.Board;
            var view = board.View;
            var hooks = UseContext(InputHooks.Current);
            var field = UseField(view.Query);
            bool editing = view.Editing.Value;
            bool asking = view.ConfirmingReset.Value;
            bool inline = p.Lay.BarInline(ClassicHomeBoard.Width.Value);
            int moreAsk = s_focusMore.Value;
            int findAsk = s_focusFind.Value;
            UseLayoutEffect(() => { if (moreAsk > 0 && !_more.IsNull) hooks.FocusNode?.Invoke(_more, true); }, DepKey.From(moreAsk));
            UseLayoutEffect(() =>
            {
                if (findAsk > 0 && inline && field.Node.Peek() is { IsNull: false } n) hooks.FocusNode?.Invoke(n, true);
            }, DepKey.From(findAsk));

            if (!editing)
            {
                var more = ToolTip.Wrap(
                    ClassicDrawerButtons.Glyph(Icons.More, Menu) with { Key = "home:tools:more", OnRealized = h => _more = h, Role = AutomationRole.Button },
                    Loc.Get(Strings.Theme.Classic.Home.Board.HomeOptions));
                return new BoxEl
                {
                    Key = "home:tools:rest", Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f,
                    Enter = new EnterExit(Opacity: 0f, Active: true), Exit = new EnterExit(Opacity: 0f, Active: true), Transition = Fade,
                    Children = [more],
                };
            }

            var undo = board.Undo.Value;
            Element[] row;
            if (asking)
                row =
                [
                    ClassicEditBarParts.Caption(Loc.Get(Strings.Theme.Classic.Home.Board.ResetAllTitle)) with { Shrink = 0f },
                    Ui.Caption(Loc.Get(Strings.Theme.Classic.Home.Board.ResetAllBody)).Secondary()
                        with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f, Grow = 1f },
                    Button.Accent(Loc.Get(Strings.Theme.Classic.Home.Board.ResetAllConfirm), () =>
                    {
                        view.ConfirmingReset.Value = false;
                        board.Verbs.Reset(HomeResetScope.Everything);
                    }) with { Key = "home:edit:reset-yes", Shrink = 0f },
                    Button.Create(Loc.Get(Strings.Theme.Classic.Home.Board.Cancel), () => view.ConfirmingReset.Value = false) with { Key = "home:edit:reset-no", Shrink = 0f },
                ];
            else
            {
                // The bar's two forms differ by the caption and the find field only (the board's width decides): those fade in and out as the declared
                // swap, and the buttons beside them glide to their new places, so a width that crosses `InlineFrom` moves nothing at once.
                var kids = new List<Element>(8) { Glide("home:edit:pencil", ClassicEditBarParts.Pencil()) };
                if (inline) kids.Add(Swap("home:edit:caption", ClassicEditBarParts.Caption(Loc.Get(Strings.Theme.Classic.Home.Board.EditHome))));
                if (inline) kids.Add(Swap("home:edit:find-slot", FindField(board, p.Lay.FindW, field) with { Key = "home:edit:find" }));
                kids.Add(Glide("home:edit:undo-slot", ClassicEditBarParts.Undo(board.Verbs.Undo, undo.CanUndo)));
                kids.Add(Glide("home:edit:redo-slot", ClassicEditBarParts.Redo(board.Verbs.Redo, undo.CanRedo)));
                kids.Add(Glide("home:edit:reset-slot", ClassicEditBarParts.Reset(Loc.Get("sidebar.edit.reset")) with
                {
                    Key = "home:edit:reset", OnClick = ResetMenu, OnRealized = h => _reset = h, Shrink = 0f,
                }));
                kids.Add(Glide("home:edit:done-slot", ClassicEditBarParts.Done(() => ClassicHomeBoard.Live?.SetEditing(false))
                    with { Key = "home:edit:done", OnRealized = h => _done = h, Shrink = 0f }));
                row = kids.ToArray();
            }
            return new BoxEl
            {
                Key = HomeBoardKeys.Edit, Direction = 0, Height = ClassicNavEditLayout.BarHeight, Shrink = 1f, MinWidth = 0f,
                AlignItems = FlexAlign.Center, Gap = ClassicNavEditLayout.Gap,
                Padding = ClassicNavEditLayout.BarPadding, Corners = Radii.ControlAll,
                Fill = Tok.FillLayerDefault, BorderWidth = 1f, BorderColor = asking ? Tok.AccentDefault : Tok.StrokeCardDefault,

                Enter = new EnterExit(Opacity: 0f, Active: true), Exit = new EnterExit(Opacity: 0f, Active: true), Transition = Fade,
                OnKeyDown = OnKey,
                Children = row,
            };
        }

        static Element Glide(string key, Element child) => new BoxEl
        {
            Key = key, Direction = 0, Shrink = 0f, AlignItems = FlexAlign.Center, Layout = ClassicTierMotion.Shove, Children = [child],
        };

        static Element Swap(string key, Element child) => new BoxEl
        {
            Key = key, Direction = 0, Shrink = 1f, MinWidth = 0f, AlignItems = FlexAlign.Center,
            Enter = ClassicTierMotion.SwapFade, Exit = ClassicTierMotion.SwapFade, Transition = ClassicTierMotion.SwapFadeMotion,
            Children = [child],
        };

        static MotionTokenDef Fade => MotionTokenDef.Eased(Design.Motion.Faster, Easing.FluentStandard, ReducedMotionPolicy.KeepFade);

        void OnKey(KeyEventArgs e)
        {
            if (e.Handled || _p is null) return;
            var board = _p.Board;
            if (e.Ctrl && !e.Alt && e.KeyCode == Keys.Z && !e.Shift) { board.Verbs.Undo(); e.Handled = true; return; }
            if (e.Ctrl && !e.Alt && (e.KeyCode == Keys.Y || e.KeyCode == Keys.Z && e.Shift)) { board.Verbs.Redo(); e.Handled = true; return; }
            if (e.KeyCode == Keys.Escape && ClassicHomeBoard.Live is { } live && live.Unwind()) e.Handled = true;
        }

        /// <summary>Home's ⋯ at rest: Edit Home; Show on Home › (what is hidden or not placed, "Nothing is hidden" when empty).</summary>
        void Menu()
        {
            if (_menu is { IsOpen: true } m) { m.Close(); return; }
            if (_p?.Board is not { Overlay: { } overlay } board) return;
            var show = new List<MenuFlyoutItem>();
            foreach (var c in board.Catalog.All())
            {
                if (c.OnHome || c.Group == HomeCatalogGroup.Library) continue;
                var cand = c;
                show.Add(new MenuFlyoutItem(c.Title, Invoke: () => board.Verbs.Add(cand)));
            }
            if (show.Count == 0) show.Add(new MenuFlyoutItem(Loc.Get("sidebar.menu.nothingHidden"), default, false));
            var items = new List<MenuFlyoutItem>
            {
                new(Loc.Get(Strings.Theme.Classic.Home.Board.EditHome), Icons.Edit, !board.ReadOnly, () => ClassicHomeBoard.Live?.SetEditing(true)),
                MenuFlyoutItem.SubMenu(Loc.Get(Strings.Theme.Classic.Home.Board.ShowOnHome), show, enabled: !board.ReadOnly),
            };
            ToolTip.CloseOpen();
            _menu = overlay.Open(() => _more, () => MenuFlyout.Create(items, () => _menu?.Close()), FlyoutPlacement.BottomEdgeAlignedRight,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss));
            _menu.ClosedAction = () => _menu = null;
        }

        /// <summary>"Reset ▾": this page at once; everything asks in the bar.</summary>
        void ResetMenu()
        {
            if (_menu is { IsOpen: true } m) { m.Close(); return; }
            if (_p?.Board is not { Overlay: { } overlay } board) return;
            var items = new List<MenuFlyoutItem>
            {
                new(Loc.Get(Strings.Theme.Classic.Home.Board.ResetPage), Icons.Refresh, Invoke: () => board.Verbs.Reset(HomeResetScope.Page)),
                new(Loc.Get(Strings.Theme.Classic.Home.Board.ResetAll), Invoke: () => board.View.ConfirmingReset.Value = true),
            };
            ToolTip.CloseOpen();
            _menu = overlay.Open(() => _reset, () => MenuFlyout.Create(items, () => _menu?.Close()), FlyoutPlacement.BottomEdgeAlignedRight,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss));
            _menu.ClosedAction = () => _menu = null;
        }
    }

    /// <summary>"Find something to add": typing opens the results under the bar (the find strip) and says the count; Enter goes to the first result;
    /// Esc clears and folds the strip, a second Esc leaves the field (Edit Home stays on).</summary>
    internal static Element FindField(HomeBoardProps board, float width, Field<string> field)
    {
        bool fill = float.IsNaN(width);
        var options = new TextBox.TextBoxOptions
        {
            Placeholder = Loc.Get(Strings.Theme.Classic.Home.Board.FindPlaceholder),
            KeepTextOnEscape = true,
            Field = field,
            OnCommit = _ => ClassicHomeFind.FocusFirst(),
            // Esc blurs the field: with text it clears and folds the strip and stays in the field; empty, focus goes to the first tile (Edit Home stays on).
            OnCancel = () =>
            {
                if (board.View.Query.Peek().Length > 0) { ClassicHomeFind.Type(board, ""); FocusFind(); return; }
                foreach (var x in board.Model.Peek().Entries)
                    if (x.Kind != BoardEntryKind.GroupHead && !x.Hidden) { board.Grid.Focus(HomeBoardKeys.Tile(x.Id)); break; }
            },
        };
        Element box = fill
            // The field takes the strip's width (a TextBox's width is a number: the measured one, re-pushed when it changes).
            ? Responsive.Of(w => TextBox.Create(board.View.Query, q => ClassicHomeFind.Type(board, q), options with { Width = MathF.Max(w, 0f) }), grow: 1f)
            : TextBox.Create(board.View.Query, q => ClassicHomeFind.Type(board, q), options with { Width = width });
        return new BoxEl
        {
            Direction = 0, Shrink = fill ? 0f : 1f, Grow = fill ? 1f : 0f, MinWidth = 0f, AlignItems = FlexAlign.Center,
            Children = [box],
        };
    }
}
