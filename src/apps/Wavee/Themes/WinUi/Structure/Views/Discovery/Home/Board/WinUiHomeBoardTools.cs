// ── Themes/WinUi/Structure/Views/Discovery/Home/Board/WinUiHomeBoardTools.cs — WinUI's `Home.BoardTools` (spec 8.1) ──────────────────────
//
// The head row's end on All: "+ Add widgets" (a toggle: it opens and folds the band under the header) and the page's ⋯ ("Undo last change" with its
// Ctrl+Z accelerator, "Reset Home": at once, with Undo on the info bar; nothing asks first). On the first visit a TeachingTip points at Add widgets
// (light dismiss off, it never blocks; closing it or opening the band remembers it, `theme.winui.state.homeTipSeen`).

using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Input;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.WinUi;

/// <summary>WinUI's board tools in the page head.</summary>
internal static class WinUiHomeBoardTools
{
    static readonly Signal<int> s_focusAdd = new(0);

    /// <summary>Return focus to "Add widgets" (the band's close, Esc).</summary>
    public static void FocusAdd() => s_focusAdd.Value = s_focusAdd.Peek() + 1;

    /// <summary>The registered template of <see cref="ViewKeys.HomeBoardTools"/>.</summary>
    public static Element Template(HomeBoardProps props, in ViewContext ctx)
        => Embed.Comp(new ToolsProps(props), static () => new Tools()) with { Key = "home:tools" };

    sealed record ToolsProps(HomeBoardProps Board);

    sealed class Tools : Component
    {
        NodeHandle _add, _more;
        OverlayHandle? _menu, _tip;
        bool _closingQuietly;
        ToolsProps? _p;

        public override Element Render()
        {
            var p = UseProps<ToolsProps>();
            _p = p;
            var board = p.Board;
            var hooks = UseContext(InputHooks.Current);
            var overlay = board.Overlay;
            bool open = board.View.Add.Value.Kind == HomeAddSurfaceKind.Band;
            bool seen = board.View.TipSeen.Value;
            int focusAsk = s_focusAdd.Value;
            // The tip points at the button: while the page is scrolled past the head it waits (it never hangs over the title bar).
            var scroll = UseScroll(board.Scroll);
            bool atHead = UseComputed(() => scroll.Offset.Value < WinUiHomeBoardRules.TipScrollMax).Value;
            UseLayoutEffect(() => { if (focusAsk > 0 && !_add.IsNull) hooks.FocusNode?.Invoke(_add, true); }, DepKey.From(focusAsk));

            // The first-run tip: shown once the button has a node, gone for good once closed or once the band opens.
            UseLayoutEffect(() =>
            {
                if (seen || open || !atHead || overlay is null || _add.IsNull) { _closingQuietly = true; _tip?.Close(); _closingQuietly = false; return; }
                if (_tip is { IsOpen: true }) return;
                _tip = TeachingTip.Show(overlay, () => _add, t =>
                {
                    t.Title = Loc.Get(Strings.Theme.Winui.Home.Board.TipTitle);
                    t.Body = Loc.Get(Strings.Theme.Winui.Home.Board.TipBody);
                    t.ActionButtonContent = Loc.Get(Strings.Theme.Winui.Home.Board.AddWidgets);
                    t.ActionButtonIsAccent = true;
                    t.ActionButtonClick = () => { board.View.MarkTipSeen(); WinUiHomeAddBand.Open(board); };
                    t.CloseButtonClick = () => { board.View.MarkTipSeen(); FocusAdd(); };
                    t.IsLightDismissEnabled = false;
                    t.PreferredPlacement = TeachingTip.PlacementMode.BottomLeft;
                });
                _tip.ClosedAction = () => { if (!_closingQuietly && !board.View.TipSeen.Peek()) board.View.MarkTipSeen(); _tip = null; };
            }, DepKey.From(seen ? 1 : 0, open ? 1 : 0, atHead ? 1 : 0, 0));

            var add = Button.Create(Loc.Get(Strings.Theme.Winui.Home.Board.AddWidgets), () => WinUiHomeAddBand.Toggle(board),
                    open ? ButtonAppearance.Accent : ButtonAppearance.Standard, ControlSize.Medium, Icons.Add)
                with { Key = "home:tools:add", OnRealized = h => _add = h, Shrink = 0f };
            var more = ToolTip.Wrap(Controls.IconAction(Icons.More, Menu) with { Key = "home:tools:more", OnRealized = h => _more = h },
                                    Loc.Get(Strings.Theme.Winui.Home.Board.PageMore));
            return new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.XS, Shrink = 0f,
                OnKeyDown = e => WinUiHomeBoard.BoardView.HandleKey(e, board),
                Children = [add, more],
            };
        }

        void Menu()
        {
            if (_menu is { IsOpen: true } m) { m.Close(); return; }
            if (_p?.Board is not { Overlay: { } overlay } board) return;
            var undo = board.Undo.Peek();
            var items = new List<MenuFlyoutItem>
            {
                new(Loc.Get(Strings.Theme.Winui.Home.Board.UndoLast), Icons.Undo, undo.CanUndo, () => board.Verbs.Undo())
                    { AcceleratorText = Loc.Get(Strings.Theme.Winui.Home.Board.UndoKeys) },
                new(Loc.Get(Strings.Theme.Winui.Home.Board.ResetHome), Icons.Refresh, !board.ReadOnly, () => board.Verbs.Reset(HomeResetScope.Page)),
            };
            ToolTip.CloseOpen();
            _menu = overlay.Open(() => _more, () => MenuFlyout.Create(items, () => _menu?.Close()), FlyoutPlacement.BottomEdgeAlignedRight,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss));
            _menu.ClosedAction = () => _menu = null;
        }
    }
}
