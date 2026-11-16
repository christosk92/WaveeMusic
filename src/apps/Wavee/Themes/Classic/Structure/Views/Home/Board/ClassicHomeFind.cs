// ── Themes/Classic/Structure/Views/Home/Board/ClassicHomeFind.cs — "Find something to add": the strip under the edit bar (spec 8.3) ─────────────
//
// Typing in the edit bar's field opens a strip UNDER the bar that sticks with it while the page scrolls (pinned right under the facet band, on the
// flyout acrylic so the tiles that slide under it never show through): a line "3 to add · 2 already on Home", then every match that is not on Home
// yet, across groups, as the add cards of the add rows (`ClassicHomeAddRow.Card`), each button saying where it goes ("Add to Now", "Add to Pinned").
// Add puts it at that group's end and rings it. No match: "Nothing matches “…”. Anything with a page can be pinned from its own menu." The count is
// spoken. The strip's height eases open and shut (a flow reveal): nothing under it jumps.
//
// At Compact the bar keeps only its buttons in the facet row; the FIELD stands in this strip, on a line of its own, for as long as Edit Home is on.

using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Input;
using FluentGpu.Localization;
using FluentGpu.Scroll.Effects;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>The find strip and the search it shows.</summary>
internal static class ClassicHomeFind
{
    static readonly Signal<int> s_focusFirst = new(0);

    /// <summary>Move focus to the first result's Add (Enter in the field; it never adds by itself).</summary>
    public static void FocusFirst() => s_focusFirst.Value = s_focusFirst.Peek() + 1;

    internal static IReadSignal<int> FirstAsk => s_focusFirst;

    /// <summary>Type into the search: the strip opens on a query and folds on an empty one; the count is said.</summary>
    public static void Type(HomeBoardProps board, string query, bool speak = true)
    {
        var view = board.View;
        if (!ReferenceEquals(view.Query.Peek(), query) && view.Query.Peek() != query) view.Query.Value = query;
        string q = query.Trim();
        view.Add.Value = q.Length > 0 ? new HomeAddSurface(HomeAddSurfaceKind.Find) : view.Add.Peek().Kind == HomeAddSurfaceKind.Find ? HomeAddSurface.Closed : view.Add.Peek();
        if (speak && q.Length > 0) board.Verbs.Say(Spoken(board, q, Results(board).Count));
    }

    /// <summary>"3 to add for mix" / "Nothing matches mix".</summary>
    public static string Spoken(HomeBoardProps board, string q, int n)
        => q.Length == 0 ? "" : n > 0 ? Strings.Theme.Classic.Home.Board.ResultsSaid(n: n, q: q) : Strings.Theme.Classic.Home.Board.NoMatchSaid(q: q);

    /// <summary>What the open add surface lists: an add row's candidates (that group's, not on Home), or the find's matches not on Home.</summary>
    public static IReadOnlyList<HomeCandidate> Results(HomeBoardProps board)
    {
        var add = board.View.Add.Peek();
        var all = board.Catalog.All();
        var list = new List<HomeCandidate>();
        switch (add.Kind)
        {
            case HomeAddSurfaceKind.Row:
                return ClassicHomeAddRow.CandidatesOf(all, add.Arg);
            case HomeAddSurfaceKind.Find:
            {
                string q = board.View.Query.Peek().Trim();
                if (q.Length == 0) return list;
                foreach (var c in board.Catalog.Find(all, q)) if (!c.OnHome) list.Add(c);
                return list;
            }
        }
        return list;
    }

    /// <summary>The strip: sticky under the facet band, in the board's column above the grid.</summary>
    public static Element Strip(HomeBoardProps board, ClassicHomeBoardLayout lay)
        => Embed.Comp(new StripProps(board, lay), static () => new StripView()) with { Key = HomeBoardKeys.Add };

    sealed record StripProps(HomeBoardProps Board, ClassicHomeBoardLayout Lay);

    sealed class StripView : Component
    {
        NodeHandle _first;

        public override Element Render()
        {
            var p = UseProps<StripProps>();
            var board = p.Board;
            var view = board.View;
            var lay = p.Lay;
            var hooks = UseContext(InputHooks.Current);
            var field = UseField(view.Query);
            bool editing = view.Editing.Value;
            string q = view.Query.Value.Trim();
            bool finding = view.Add.Value.Kind == HomeAddSurfaceKind.Find && q.Length > 0;
            int findAsk = ClassicHomeEditBar.FindAsk.Value;
            int firstAsk = s_focusFirst.Value;
            UseLayoutEffect(() =>
            {
                if (findAsk > 0 && !lay.BarInline && field.Node.Peek() is { IsNull: false } n) hooks.FocusNode?.Invoke(n, true);
            }, DepKey.From(findAsk));
            UseLayoutEffect(() => { if (firstAsk > 0 && !_first.IsNull) hooks.FocusNode?.Invoke(_first, true); }, DepKey.From(firstAsk));

            var kids = new List<Element>(3);
            if (editing && !lay.BarInline)
                kids.Add(ClassicHomeEditBar.FindField(board, float.NaN, field) with { Key = "home:find:field" });
            if (editing && finding)
            {
                var all = board.Catalog.All();
                var hits = board.Catalog.Find(all, q);
                var counts = board.Catalog.Count(hits);
                var toAdd = new List<HomeCandidate>(hits.Count);
                foreach (var c in hits) if (!c.OnHome) toAdd.Add(c);
                _first = default;
                if (toAdd.Count == 0)
                    kids.Add(Ui.Body(Strings.Theme.Classic.Home.Board.NoMatch(q: q)).Secondary()
                        with { Key = "home:find:none", Wrap = TextWrap.Wrap, MinWidth = 0f });
                else
                {
                    kids.Add(Ui.Caption(Strings.Theme.Classic.Home.Board.FindCount(add: counts.ToAdd, on: counts.OnHome)).Secondary()
                        with { Key = "home:find:count", MinHeight = ClassicHomeBoardRules.FindCountH, MinWidth = 0f });
                    var cards = new Element[toAdd.Count];
                    for (int i = 0; i < cards.Length; i++)
                    {
                        var c = toAdd[i];
                        string label = Strings.Theme.Classic.Home.Board.AddTo(group: ClassicHomeBoard.GroupTitleOf(board, c.TargetGroupId));
                        cards[i] = ClassicHomeAddRow.Card(board, lay, c, label, i == 0 ? h => _first = h : null);
                    }
                    kids.Add(Ui.ScrollView(new BoxEl { Direction = 0, Gap = lay.Gap, Children = cards }, horizontal: true)
                        with { Key = "home:find:row", MinWidth = 0f, Height = ClassicHomeBoardRules.FindRowH, Grow = 0f });
                }
            }
            bool open = kids.Count > 0;
            return new BoxEl
            {
                Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch, ClipToBounds = true,
                Layout = ClassicHomeBoardRules.Reveal,
                Children = open
                    ?
                    [
                        new BoxEl
                        {
                            Key = "home:find", Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch, Gap = Spacing.S,
                            Padding = new Edges4(Spacing.M, Spacing.S, Spacing.M, Spacing.M), Margin = new Edges4(0f, 0f, 0f, lay.Gap),
                            Corners = Radii.CardAll, BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f,
                            Fill = Prop.Of(static () => Tok.AcrylicFlyout.Fallback), Acrylic = Tok.AcrylicFlyout,
                            Children = kids.ToArray(),
                        },
                    ]
                    : [],
            }.Sticky(ClassicHomeRules.StuckBottom, scope: ClassicHomeRules.PageScope);
        }
    }
}
