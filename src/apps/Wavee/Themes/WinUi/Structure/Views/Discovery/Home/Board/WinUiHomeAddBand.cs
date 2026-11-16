// ── Themes/WinUi/Structure/Views/Discovery/Home/Board/WinUiHomeAddBand.cs — "Add widgets": a band under the header (spec 8.1) ──────────────
//
// Windows' Widgets board adds through a picker dialog; here (owner, 2026-11-16) it is a band in the page, built from stock parts: an Expander-style
// reveal (its height eases, the board slides under it), a SelectorBar (Widgets / From Spotify / Your library / Extensions; "All results n" and a count per
// kind while searching), an AutoSuggestBox-style search with no suggestion popup (the band's own row is the result list), and a horizontal row of LIVE
// candidates: each is the real tile at the chosen size, with RadioButtons for the size and Pin. What is not on Home yet comes first. Pin lands the
// item at the end of its group, ringed; the candidate then reads "Pinned"; the band stays open. A candidate is also a drag source the board accepts.
//
// Keys: Tab into the band, ← / → on the selector bar, Enter in the search moves focus to the first result's Pin (it never pins by itself), Esc clears the
// text, a second Esc closes the band and returns focus to "Add widgets". "/" anywhere on Home opens the band on its search.

using System.Collections.Generic;
using System.Collections.Immutable;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Input;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.WinUi;

/// <summary>WinUI's add band.</summary>
internal static class WinUiHomeAddBand
{
    /// <summary>The drag kind of a candidate.</summary>
    public const string DragKind = "home.candidate";

    /// <summary>"/" (VK_OEM_2).</summary>
    public const int SlashKey = WinUiHomeBoardRules.SlashKey;

    static readonly Dictionary<string, HomeSize> s_size = new(StringComparer.Ordinal);
    static readonly Signal<int> s_focusSearch = new(0);
    static readonly Signal<int> s_kind = new(0);
    static int s_kindBeforeSearch;
    static HomeBoardProps? s_board;

    /// <summary>The size picked on a candidate (auto until one is chosen).</summary>
    public static HomeSize SizeChosen(HomeCandidate c) => s_size.TryGetValue(c.Id, out var s) ? s : HomeSize.Auto;

    /// <summary>A text editor holds focus (the single-key shortcuts stand aside).</summary>
    public static bool EditorFocused() => s_board?.EditorFocused() ?? false;

    /// <summary>Open the band (or keep it open) and put focus in its search.</summary>
    public static void OpenSearch(HomeBoardProps board)
    {
        Open(board);
        s_focusSearch.Value = s_focusSearch.Peek() + 1;
    }

    /// <summary>Open the band.</summary>
    public static void Open(HomeBoardProps board)
    {
        if (board.View.Add.Peek().Kind == HomeAddSurfaceKind.Band) return;
        board.View.Add.Value = new HomeAddSurface(HomeAddSurfaceKind.Band, KindId(s_kind.Peek()));
        board.View.MarkTipSeen();
        board.Verbs.Say(Loc.Get(Strings.Theme.Winui.Home.Board.BandOpened));
    }

    /// <summary>Close the band (clearing its search) and return focus to "Add widgets".</summary>
    public static void Close(HomeBoardProps board)
    {
        if (board.View.Add.Peek().Kind != HomeAddSurfaceKind.Band) return;
        if (board.View.Query.Peek().Length > 0) { board.View.Query.Value = ""; s_kind.Value = s_kindBeforeSearch; }
        board.View.Add.Value = HomeAddSurface.Closed;
        board.Verbs.Say(Loc.Get(Strings.Theme.Winui.Home.Board.BandClosed));
        WinUiHomeBoardTools.FocusAdd();
    }

    /// <summary>Toggle the band.</summary>
    public static void Toggle(HomeBoardProps board)
    {
        if (board.View.Add.Peek().Kind == HomeAddSurfaceKind.Band) Close(board);
        else Open(board);
    }

    static string KindId(int i) => i switch { 1 => "spotify", 2 => "library", 3 => "extensions", _ => "widgets" };

    /// <summary>The disclosure: keyed `home:add`, its height eases open and shut (the board under it slides).</summary>
    public static BoxEl Disclosure(HomeBoardProps board, WinUiHomeBoardLayout lay, bool open)
    {
        s_board = board;
        return new BoxEl
        {
            Key = HomeBoardKeys.Add, Direction = 1, MinWidth = 0f, ClipToBounds = true, AlignItems = FlexAlign.Stretch,
            Layout = Design.Reduced ? WinUiHomeBoardRules.BandFade : WinUiHomeBoardRules.BandReveal,
            Children = open ? [Embed.Comp(new BandProps(board, lay), static () => new Band()) with { Key = "home:add:band" }] : [],
        };
    }

    sealed record BandProps(HomeBoardProps Board, WinUiHomeBoardLayout Lay);

    /// <summary>The open band.</summary>
    sealed class Band : Component
    {
        BandProps? _p;
        NodeHandle _firstPin;
        string _lastQuery = "";
        bool _justCleared;   // the search box cleared itself on this Escape: that Escape does not also close the band

        public override Element Render()
        {
            var p = UseProps<BandProps>();
            _p = p;
            var board = p.Board;
            var lay = p.Lay;
            var hooks = UseContext(InputHooks.Current);
            var field = UseField(board.View.Query);
            int focusAsk = s_focusSearch.Value;
            UseLayoutEffect(() =>
            {
                if (focusAsk > 0 && field.Node.Peek() is { IsNull: false } n) hooks.FocusNode?.Invoke(n, true);
            }, DepKey.From(focusAsk));

            string q = board.View.Query.Value.Trim();
            var all = board.Catalog.All();
            var hits = q.Length > 0 ? board.Catalog.Find(all, q) : all;
            var counts = board.Catalog.Count(hits);
            int kind = s_kind.Value;
            bool searching = q.Length > 0;

            // The selector: the four kinds; while searching "All results n" first and each kind with its count.
            var labels = new List<string>(5);
            if (searching) labels.Add(Strings.Theme.Winui.Home.Board.AllResults(n: hits.Count));
            for (int g = 0; g < 4; g++)
            {
                string word = Loc.Get(g switch
                {
                    1 => Strings.Theme.Winui.Home.Board.KindSpotify, 2 => Strings.Theme.Winui.Home.Board.KindLibrary,
                    3 => Strings.Theme.Winui.Home.Board.KindExtensions, _ => Strings.Theme.Winui.Home.Board.KindWidgets,
                });
                labels.Add(searching ? Strings.Theme.Winui.Home.Board.KindCount(kind: word, n: counts.Of((HomeCatalogGroup)g)) : word);
            }
            int selected = searching ? (kind < 0 ? 0 : kind + 1) : Math.Max(0, kind);
            var selector = SelectorBar.Create(labels, new Signal<int>(selected), i =>
            {
                if (searching) s_kind.Value = i - 1; else s_kind.Value = i;
                board.View.Add.Value = new HomeAddSurface(HomeAddSurfaceKind.Band, KindId(Math.Max(0, s_kind.Peek())));
            }) with { Key = "home:add:kinds" };

            var search = AutoSuggestBox.Create([], Loc.Get(Strings.Theme.Winui.Home.Board.SearchPlaceholder), WinUiHomeBoardRules.SearchMaxW,
                text: board.View.Query, onChange: OnQuery, onQuerySubmitted: _ => FocusFirstPin(hooks), field: field, debounceMs: 0f,
                grow: 1f, maxFillWidth: WinUiHomeBoardRules.SearchMaxW, suggestionPresentation: AutoSuggestBoxSuggestionPresentation.Inline);

            var close = ToolTip.Wrap(Controls.IconAction(Icons.ChromeClose, () => Close(board)) with { Key = "home:add:close" },
                                     Loc.Get(Strings.Theme.Winui.Home.Board.CloseBand));

            // The candidates of the kind (all of them while searching with "All results"), what is not on Home first.
            var shown = new List<HomeCandidate>(hits.Count);
            int want = searching ? kind : Math.Max(0, kind);
            for (int pass = 0; pass < 2; pass++)
                foreach (var c in hits)
                    if (c.OnHome == (pass == 1) && (want < 0 || (int)c.Group == want)) shown.Add(c);

            Element row;
            if (shown.Count == 0)
                row = new BoxEl
                {
                    Key = "home:add:none", Direction = 1, MinWidth = 0f, Padding = new Edges4(0f, Spacing.M, 0f, Spacing.M),
                    Children = [Ui.Body(searching ? Strings.Theme.Winui.Home.Board.NoMatch(q: q) : Loc.Get(Strings.Theme.Winui.Home.Board.NothingOfKind)).Secondary()
                                 ],
                };
            else
            {
                var kids = new Element[shown.Count];
                for (int i = 0; i < kids.Length; i++) kids[i] = Candidate(board, lay, shown[i], i == 0 ? h => _firstPin = h : null);
                row = Ui.ScrollView(new BoxEl { Direction = 0, Gap = WinUiHomeBoardRules.BandRowGap, Children = kids }, horizontal: true)
                    with { Key = "home:add:row", MinWidth = 0f };
            }

            return new BoxEl
            {
                Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
                Padding = new Edges4(0f, 0f, 0f, WinUiHomeBoardRules.BandFoot),
                // The band's content fades in while its box opens (the Expander's content transition); the box's own height is the reveal.
                Enter = new EnterExit(Opacity: 0f, Active: true), Exit = new EnterExit(Opacity: 0f, Active: true),
                Transition = MotionTokenDef.Eased(Design.Motion.Fast, Easing.FluentStandard, ReducedMotionPolicy.KeepFade),
                OnKeyDown = e => OnKey(e, board),
                Children =
                [
                    new BoxEl
                    {
                        Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch, Gap = Spacing.S, Corners = Radii.CardAll,
                        Fill = Tok.FillCardSecondary, BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f,
                        Padding = new Edges4(WinUiHomeBoardRules.BandPadX, WinUiHomeBoardRules.BandPadTop, WinUiHomeBoardRules.BandPadX, WinUiHomeBoardRules.BandPadBottom),
                        Children =
                        [
                            new BoxEl
                            {
                                Key = "home:add:head", Direction = 0, MinWidth = 0f, AlignItems = FlexAlign.Center, Gap = Spacing.S, Wrap = true,
                                MinHeight = WinUiHomeBoardRules.BandHeadH,
                                Children =
                                [
                                    new BoxEl { Direction = 1, Shrink = 1f, MinWidth = 0f, Children = [selector] },
                                    new BoxEl { Key = "home:add:search", Direction = 1, Width = WinUiHomeBoardRules.SearchMaxW, Shrink = 1f, MinWidth = 0f, Children = [search] },
                                    close,
                                ],
                            },
                            row,
                            Ui.Caption(Loc.Get(Strings.Theme.Winui.Home.Board.BandHint)).Secondary() with { Wrap = TextWrap.Wrap, MinWidth = 0f },
                        ],
                    },
                ],
            };
        }

        void OnQuery(string text)
        {
            if (_p is null) return;
            string q = text.Trim();
            var board = _p.Board;
            // Typing the first character switches the selector to "All results"; clearing it returns to the kind it was on.
            bool had = _lastQuery.Length > 0;
            _lastQuery = q;
            if (q.Length > 0 && !had) { s_kindBeforeSearch = Math.Max(0, s_kind.Peek()); s_kind.Value = -1; }
            else if (q.Length == 0 && had) s_kind.Value = s_kindBeforeSearch;
            _justCleared = q.Length == 0 && had;
            if (q.Length > 0)
            {
                int n = board.Catalog.Find(board.Catalog.All(), q).Count;
                board.Verbs.Say(n > 0 ? Strings.Theme.Winui.Home.Board.ResultsSaid(n: n, q: q) : Strings.Theme.Winui.Home.Board.NoMatchSaid(q: q));
            }
        }

        void FocusFirstPin(InputHooks hooks)
        {
            if (!_firstPin.IsNull) hooks.FocusNode?.Invoke(_firstPin, true);
        }

        /// <summary>Esc clears the search first, then closes the band.</summary>
        void OnKey(KeyEventArgs e, HomeBoardProps board)
        {
            if (e.Handled || e.KeyCode != Keys.Escape) return;
            if (_justCleared) { _justCleared = false; e.Handled = true; return; }
            if (board.View.Query.Peek().Length > 0) { board.View.Query.Value = ""; _lastQuery = ""; s_kind.Value = s_kindBeforeSearch; }
            else Close(board);
            e.Handled = true;
        }
    }

    /// <summary>One live candidate: the real tile at the chosen size, its name and line, the sizes, Pin (or "Pinned"). A drag source.</summary>
    static Element Candidate(HomeBoardProps board, WinUiHomeBoardLayout lay, HomeCandidate c, Action<NodeHandle>? onPin)
    {
        var sizes = board.Catalog.Sizes(c);
        var chosen = SizeChosen(c);
        var size = chosen == HomeSize.Auto ? board.Catalog.AutoSize(c) : chosen;
        bool on = c.OnHome;
        var kids = new List<Element>(5)
        {
            new BoxEl
            {
                Width = lay.PreviewW, Height = WinUiHomeBoardRules.PreviewH, Shrink = 0f, ClipToBounds = true, Corners = Radii.CardAll, HitTestVisible = false,
                Direction = 1, Children = [board.Catalog.Preview(c, size, lay.PreviewW, WinUiHomeBoardRules.PreviewH)],
            },
            Ui.BodyStrong(c.Title) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
            Ui.Caption(c.Line).Secondary() with { MaxLines = 2, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
        };
        if (sizes.Length > 1)
        {
            var labels = new string[sizes.Length];
            int at = 0;
            for (int i = 0; i < sizes.Length; i++) { labels[i] = WinUiHomeTileMenu.SizeLabel(sizes[i]); if (sizes[i] == size) at = i; }
            var arr = sizes;
            string id = c.Id;
            kids.Add(RadioButtons.Create(labels, new Signal<int>(at), i => { s_size[id] = arr[i]; s_kind.Value = s_kind.Peek(); },
                                         maxColumns: 1, isEnabled: !on));
        }
        var pin = (on
            ? Button.Create(Loc.Get(Strings.Theme.Winui.Home.Board.Pinned), static () => { }, ButtonAppearance.Standard, ControlSize.Medium, isEnabled: false)
            : Button.Accent(Loc.Get(Strings.Theme.Winui.Home.Board.Pin), () => board.Verbs.Add(c, -1, SizeChosen(c))))
            with { Key = "home:add:pin:" + c.Id, AlignSelf = FlexAlign.Start, OnRealized = onPin };
        kids.Add(new BoxEl { Grow = 1f });
        kids.Add(pin);
        var payload = c;
        // The candidate is dragged DOWN onto Home: its draggable box stands in a column of its own, so a vertical gesture runs along the item's
        // axis and the drag wins over the page's pan; a sideways gesture still scrolls the row (the engine's arena-lite rule).
        return new BoxEl
        {
            Key = "home:add:cand:" + c.Id, Direction = 1, Shrink = 0f, MinWidth = 0f,
            Children =
            [
                new BoxEl
                {
            Direction = 1, Width = lay.CandidateW, Shrink = 0f, Gap = Spacing.S, MinWidth = 0f,
            Padding = new Edges4(Spacing.M, Spacing.M, Spacing.M, Spacing.M), Corners = Radii.CardAll,
            Fill = Tok.FillCardDefault, BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f,
            Draggable = on ? null : new DragSource(DragKind, () => payload),
            Children = kids.ToArray(),
                },
            ],
        };
    }
}
