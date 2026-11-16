// ── Themes/Zune/Structure/Views/Home/Board/ZuneHomeAppBar.cs — Zune's Home application bar (`Home.BoardTools`; spec 8.2 "The bar") ─────────────
//
// Windows Phone's Application Bar at the foot of Home, keyed `home:edit`. The page lays it over its foot (outside the scroller, so it stays put while
// the page scrolls; the board reserves its height under its last row). It shows ONLY WHILE CUSTOMIZING (owner, 2026-11-16: "edit toolbar, dont
// show it every time only when we click on edit somewhere in zune"): it slides up from the foot when customize begins (the "edit" word by the page
// title, a held press, a right-click or Enter on a tile) and slides away on done, Escape or a tap away. It holds **add** (all items), **options**
// (turns the selected tile over; enabled when the selected tile has options) and **done**. Its ⋯ slides it up to show the buttons' labels and the
// menu words ("reset home": at once, undo is by the page title). No undo here (owner: undo is by the title). Never over all items.
//
//   customizing  (add) (options) (done)              ⋯
//   ⋯ open       the same rings with their labels under them, then "reset home" in the 24/30 light voice

using System.Collections.Generic;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using B = Wavee.Themes.Zune.ZuneHomeBoardRules;

namespace Wavee.Themes.Zune;

/// <summary>Zune's Home app bar.</summary>
internal static class ZuneHomeAppBar
{
    /// <summary>The registered template of <see cref="ViewKeys.HomeBoardTools"/>: the bar, laid by the page over its foot.</summary>
    public static Element Template(HomeBoardProps props, in ViewContext ctx)
        => Embed.Comp(props, static () => new BarView()) with { Key = "home:edit-host" };

    sealed class BarView : Component
    {
        readonly Signal<bool> _open = new(false);
        NodeHandle _more;
        InputHooks? _hooks;

        public override Element Render()
        {
            var board = UseProps<HomeBoardProps>();
            _hooks = UseContext(InputHooks.Current);
            var view = board.View;
            var verbs = board.Verbs;
            bool editing = view.Editing.Value;
            bool all = view.Add.Value.Kind == HomeAddSurfaceKind.AllItems;
            bool open = _open.Value;
            string? sel = view.SelectedId.Value;
            bool turned = sel is not null && view.OpenId.Value == sel;
            bool canTurn = ZuneHomeBoard.CanTurn(board.Model.Value, sel);

            var buttons = new List<Element>(3);
            {
                buttons.Add(Button("add", Icons.Add, Loc.Get(Strings.Theme.Zune.Home.Add), open, true, false, () => { Close(); ZuneHomeAllItems.Open(board); }));
                buttons.Add(Button("options", Icons.Settings, Loc.Get(Strings.Theme.Zune.Home.Options), open, canTurn, turned, () => { Close(); ZuneHomeBoard.ToggleOptions(view, verbs); }));
                buttons.Add(Button("done", Icons.Accept, Loc.Get(Strings.Theme.Zune.Home.Done), open, true, false, () => { Close(); ZuneHomeBoard.Done(view, verbs); }));
            }

            var kids = new List<Element>(2)
            {
                new BoxEl
                {
                    Key = "home:edit:row", Direction = 0, ZStack = true, MinWidth = 0f, MinHeight = B.BarH,
                    Children =
                    [
                        new BoxEl
                        {
                            Direction = 0, Justify = FlexJustify.Center, AlignItems = FlexAlign.Start, Gap = B.BarGap, MinWidth = 0f,
                            Padding = new Edges4(B.BarMoreW, B.BarPadTop, B.BarMoreW, 0f),
                            Children = buttons.ToArray(),
                        },
                        new BoxEl
                        {
                            Direction = 0, Justify = FlexJustify.End, AlignItems = FlexAlign.Start, HitTestPassThrough = true,
                            Children =
                            [
                                Controls.Named(new BoxEl
                                {
                                    Key = B.BarMoreKey, OnRealized = h => _more = h, Width = B.BarMoreW, Height = B.BarMoreH, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                                    Focusable = true, Role = AutomationRole.Button, Cursor = CursorId.Hand, OnClick = () => _open.Value = !_open.Peek(),
                                    Children = [new TextEl(Icons.More) { Size = B.BarGlyph, FontFamily = Tok.Definition.Fonts.Icon, Color = Tok.TextPrimary }],
                                }, Loc.Get(Strings.Theme.Zune.Home.More)),
                            ],
                        },
                    ],
                },
            };
            if (open)
                kids.Add(new BoxEl
                {
                    Key = B.BarMenuKey, Direction = 1, MinWidth = 0f, Padding = new Edges4(0f, 0f, 0f, B.BarMenuFoot),
                    Enter = new EnterExit(Opacity: 0f, Active: true), Exit = new EnterExit(Opacity: 0f, Active: true),
                    Children =
                    [
                        MenuWord("home:edit:reset", Loc.Get(Strings.Theme.Zune.Home.ResetHome), () =>
                        {
                            Close();
                            verbs.Reset(HomeResetScope.Page);
                            verbs.Say(Loc.Get(Strings.Theme.Zune.Home.ResetSaid));
                        }),
                    ],
                });

            Element bar = new BoxEl
            {
                Key = HomeBoardKeys.Edit, Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
                Fill = R.Layer.FloatingChrome,
                Layout = B.BarReveal,
                // Windows Phone's app bar slides up from the foot and back down; reduced motion fades.
                Enter = new EnterExit(Opacity: Design.Reduced ? 0f : 1f, Dy: Design.Reduced ? 0f : B.BarH, Active: true),
                Exit = new EnterExit(Opacity: Design.Reduced ? 0f : 1f, Dy: Design.Reduced ? 0f : B.BarH, Active: true),
                Transition = MotionTokenDef.Eased(B.RecedeMs, Easing.FluentStandard, ReducedMotionPolicy.KeepFade),
                OnKeyDown = e => OnKey(e, board),
                Children = kids.ToArray(),
            };
            // A layer over the page's foot: only the bar takes the pointer. It is there only while customizing (owner, 2026-11-16), and never over
            // all items (its back ring is the way out); the page reserves its height under the board, so the last tiles stay reachable.
            return new BoxEl
            {
                Direction = 1, Justify = FlexJustify.End, MinWidth = 0f, HitTestPassThrough = true, AlignItems = FlexAlign.Stretch,
                Children = editing && !all ? [bar] : [],
            };
        }

        /// <summary>Close the menu; focus returns to the ⋯ (its word is gone), so the bar keeps the keys (Escape, Ctrl+Z).</summary>
        void Close()
        {
            if (!_open.Peek()) return;
            _open.Value = false;
            if (!_more.IsNull) _hooks?.FocusNode?.Invoke(_more, true);
        }

        void OnKey(KeyEventArgs e, HomeBoardProps board)
        {
            if (e.Handled) return;
            if (e.Ctrl && !e.Shift && !e.Alt && e.KeyCode == Keys.Z) { board.Verbs.Undo(); e.Handled = true; return; }
            if (e.KeyCode != Keys.Escape) return;
            if (_open.Peek()) { _open.Value = false; e.Handled = true; return; }
            if (board.View.Editing.Peek()) { ZuneHomeBoard.Done(board.View, board.Verbs); e.Handled = true; }
        }

        /// <summary>One bar button: a 48 ring with its glyph (filled in the ink while pressed or on), its label under it while the bar is open.</summary>
        static Element Button(string id, string glyph, string label, bool open, bool enabled, bool on, Action act)
        {
            var ring = new BoxEl
            {
                Width = B.BarRing, Height = B.BarRing, Shrink = 0f, Corners = CornerRadius4.All(B.BarRing / 2f), Direction = 0,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, HitTestVisible = false,
                BorderWidth = B.BarRingStroke, BorderColor = Tok.TextPrimary,
                Fill = on ? Tok.TextPrimary : ColorF.Transparent,
                Children = [new TextEl(glyph) { Size = B.BarGlyph, FontFamily = Tok.Definition.Fonts.Icon, Color = on ? Tok.FillSolidBase : Tok.TextPrimary }],
            };
            var kids = open
                ? new Element[]
                {
                    ring,
                    R.Type.MicroMeta(label) with { Case = R.Case.For(LabelKind.Command), MaxLines = 1, Wrap = TextWrap.NoWrap, Color = Tok.TextPrimary },
                }
                : [ring];
            return Controls.Named(new BoxEl
            {
                Key = "home:edit:" + id, Direction = 1, AlignItems = FlexAlign.Center, Gap = B.LabelGap, Width = B.BarButtonW, Shrink = 0f,
                Focusable = enabled, Role = AutomationRole.Button, Cursor = enabled ? CursorId.Hand : null,
                OnClick = enabled ? act : null, Opacity = enabled ? 1f : 0.35f,
                HoverFill = ColorF.Transparent, PressedFill = ColorF.Transparent,
                Children = kids,
            }, label);
        }

        /// <summary>A menu word: the 24/30 light voice, lowercase, padded 9 / 24, the accent on hover.</summary>
        static Element MenuWord(string key, string text, Action act) => new BoxEl
        {
            Key = key, Direction = 0, MinWidth = 0f, Padding = new Edges4(B.BarMenuPadX, B.BarMenuPadY, B.BarMenuPadX, B.BarMenuPadY),
            Focusable = true, Role = AutomationRole.MenuItem, Cursor = CursorId.Hand, OnClick = act,
            Children = [R.Type.SheetTitle(text) with { Case = R.Case.For(LabelKind.Command), HoverColor = Tok.AccentTextPrimary, MaxLines = 1 }],
        };
    }
}
