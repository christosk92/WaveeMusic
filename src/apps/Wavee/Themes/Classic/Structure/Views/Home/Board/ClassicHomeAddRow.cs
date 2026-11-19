// ── Themes/Classic/Structure/Views/Home/Board/ClassicHomeAddRow.cs — "Add to {group}": the row at every group's end in Edit Home (spec 8.3) ─────
//
// In Edit Home every group ends in an add row. It opens IN PLACE into a strip of what that group can hold and does not show: hidden sections, widgets not
// placed, and for Pinned a few library suggestions; each as an add card (glyph or cover, name, one line, "Add"). Add puts it at the group's end and rings
// it; a toast says so with Undo. One add row is open at a time (`HomeBoardView.Add` = Row(groupId)). The row is a pack `Row` whose height is the closed
// row's or the open strip's, so opening and closing re-pack the grid: the rows under it slide by the 250 ms re-flow, nothing snaps.

using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Input;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>A group's add row.</summary>
internal static class ClassicHomeAddRow
{
    static readonly FluentGpu.Signals.Signal<(string Group, int Ask)> s_focusButton = new(("", 0));

    /// <summary>Return focus to a group's "Add to …" button (its strip folded by Esc).</summary>
    public static void FocusButton(string groupId) => s_focusButton.Value = (groupId, s_focusButton.Peek().Ask + 1);

    /// <summary>How many library suggestions Pinned's row offers.</summary>
    const int LibraryMax = 8;

    /// <summary>What group <paramref name="groupId"/> can hold and does not show: its own candidates not on Home; Pinned adds a few library items.</summary>
    public static IReadOnlyList<HomeCandidate> CandidatesOf(IReadOnlyList<HomeCandidate> all, string groupId)
    {
        var list = new List<HomeCandidate>();
        int library = 0;
        foreach (var c in all)
        {
            if (c.OnHome || c.TargetGroupId != groupId) continue;
            if (c.Group == HomeCatalogGroup.Library && library++ >= LibraryMax) continue;
            list.Add(c);
        }
        return list;
    }

    /// <summary>The add row of <paramref name="group"/>'s group (its head entry).</summary>
    public static Element Create(HomeBoardProps board, ClassicHomeBoardLayout lay, BoardEntry group)
        => Embed.Comp(new RowProps(board, lay, group.GroupId, group.Title), static () => new Row()) with { Key = "home:addrow:" + group.GroupId + ":view" };

    sealed record RowProps(HomeBoardProps Board, ClassicHomeBoardLayout Lay, string GroupId, string Title);

    sealed class Row : Component
    {
        NodeHandle _first, _button;
        readonly FluentGpu.Signals.Signal<bool> _settled = new(false);

        public override Element Render()
        {
            var p = UseProps<RowProps>();
            var board = p.Board;
            var view = board.View;
            var hooks = UseContext(InputHooks.Current);
            var add = view.Add.Value;
            bool open = add.Kind == HomeAddSurfaceKind.Row && add.Arg == p.GroupId;
            int firstAsk = ClassicHomeFind.FirstAsk.Value;
            // The cards come once the row has grown to its open height (the grid's re-flow), and fade in there: nothing is uncovered by a moving
            // edge. Closing takes them away at once, then the row folds.
            bool settled = _settled.Value;
            UseTimeout(() => _settled.Value = open, open ? ClassicTierMotion.ReflowMs : 0f, DepKey.From(open ? 1 : 0));
            if (!open && settled) settled = false;
            UseLayoutEffect(() => { if (open && settled && !_first.IsNull) hooks.FocusNode?.Invoke(_first, true); }, DepKey.From(open && settled ? 1 : 0, firstAsk));
            string groupId = p.GroupId;
            var ask = s_focusButton.Value;
            UseLayoutEffect(() => { if (ask.Ask > 0 && ask.Group == groupId && !_button.IsNull) hooks.FocusNode?.Invoke(_button, true); }, DepKey.From(ask.Ask));
            var toggle = Button.Create(Strings.Theme.Classic.Home.Board.AddTo(group: p.Title),
                () => view.Add.Value = open ? HomeAddSurface.Closed : new HomeAddSurface(HomeAddSurfaceKind.Row, groupId),
                ButtonAppearance.Subtle, ControlSize.Medium, open ? Icons.ChevronUp : Icons.Add)
                with { Key = "home:addrow:" + groupId + ":button", OnRealized = h => _button = h, Shrink = 0f, AlignSelf = FlexAlign.Start, MinHeight = p.Lay.AddRow };
            var kids = new List<Element>(2) { toggle };
            _first = default;
            if (open && settled)
            {
                var cands = CandidatesOf(board.Catalog.All(), groupId);
                if (cands.Count == 0)
                    kids.Add(Ui.Body(Loc.Get(Strings.Theme.Classic.Home.Board.NothingToAdd)).Secondary() with { Key = "home:addrow:none", MinWidth = 0f });
                else
                {
                    var cards = new Element[cands.Count];
                    string label = Loc.Get(Strings.Theme.Classic.Home.Board.Add);
                    for (int i = 0; i < cards.Length; i++) cards[i] = Card(board, p.Lay, cands[i], label, i == 0 ? h => _first = h : null);
                    kids.Add(Ui.ScrollView(new BoxEl { Direction = 0, Gap = p.Lay.Gap, Children = cards }, horizontal: true)
                        with
                        {
                            Key = "home:addrow:strip", MinWidth = 0f, Height = ClassicHomeBoardRules.FindRowH, Grow = 0f,
                            Enter = new EnterExit(Opacity: 0f, Active: true), Transition = ClassicHomeBoardRules.FadeIn,
                        });
                }
            }
            return new BoxEl
            {
                Direction = 1, Grow = 1f, MinWidth = 0f, AlignItems = FlexAlign.Stretch, Gap = Spacing.S,
                Corners = Radii.CardAll, BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f,
                BorderDashOn = ClassicHomeBoardRules.DashOn, BorderDashOff = ClassicHomeBoardRules.DashOff,
                Padding = open ? new Edges4(Spacing.S, 0f, Spacing.S, Spacing.S) : default,
                Children = kids.ToArray(),
            };
        }
    }

    /// <summary>One add card: its cover or glyph, its name, one line (what it is), and its button ("Add", or "Add to {group}" in the find strip).</summary>
    public static Element Card(HomeBoardProps board, ClassicHomeBoardLayout lay, HomeCandidate c, string label, Action<NodeHandle>? onAdd)
    {
        float art = ClassicHomeBoardRules.AddArt;
        Element cover = c.Cover is { Length: > 0 } url
            ? new BoxEl { Shrink = 0f, Width = art, Height = art, Children = [Ui.Image(url, art, art, c.Round ? art * ClassicHomeBoardRules.Half : Radii.Control, Tok.FillSubtleSecondary)] }
            : new BoxEl { Shrink = 0f, Width = art, Height = art, Children = [Controls.IconPlate(GlyphOf(c), art, Tok.FillSubtleSecondary, Tok.TextSecondary)] };
        var cand = c;
        return new BoxEl
        {
            Key = "home:add:card:" + c.Id, Direction = 1, Width = lay.AddCardW, Shrink = 0f, Gap = Spacing.S, MinWidth = 0f,
            Padding = new Edges4(Spacing.M, Spacing.M, Spacing.M, Spacing.M), Corners = Radii.CardAll,
            Fill = Tok.FillCardDefault, BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f,
            Children =
            [
                new BoxEl
                {
                    Direction = 0, Gap = Spacing.M, MinWidth = 0f, AlignItems = FlexAlign.Center,
                    Children =
                    [
                        cover,
                        new BoxEl
                        {
                            Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f,
                            Children =
                            [
                                Ui.BodyStrong(c.Title) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                                Ui.Caption(KindWord(c) + (c.Line.Length > 0 ? " · " + c.Line : "")).Secondary()
                                    with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                            ],
                        },
                    ],
                },
                Button.Create(label, () => board.Verbs.Add(cand), ButtonAppearance.Standard, ControlSize.Small, Icons.Add)
                    with { Key = "home:add:card:" + c.Id + ":add", AlignSelf = FlexAlign.Start, OnRealized = onAdd },
            ],
        };
    }

    static string KindWord(HomeCandidate c) => Loc.Get(c.Group switch
    {
        HomeCatalogGroup.Spotify => Strings.Theme.Classic.Home.Board.KindSpotify,
        HomeCatalogGroup.Library => Strings.Theme.Classic.Home.Board.KindLibrary,
        HomeCatalogGroup.Extensions => Strings.Theme.Classic.Home.Board.KindExtension,
        _ => Strings.Theme.Classic.Home.Board.KindWidget,
    });

    static string GlyphOf(HomeCandidate c) => c.Source switch
    {
        WidgetSource { Widget: "nowPlaying" } => Icons.Play,
        WidgetSource { Widget: "daylist" } => Icons.Clock,
        WidgetSource { Widget: "recents" } => Icons.History,
        WidgetSource { Widget: "upNext" } => Icons.Queue,
        WidgetSource { Widget: "releases" } => Icons.RefineSparkle,
        SpotifySectionSource => Icons.Library,
        _ => Icons.Pin,
    };
}
