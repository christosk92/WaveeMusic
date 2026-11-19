// ── Themes/Classic/Structure/Views/Screens/ClassicSettings.Search.cs — the masthead's "Find a setting" box, its popup and the results page (7.1) ─────
//
// THE BOX (design 4.8, 7.1; board C-Home). The stock AutoSuggestBox, bound two-way to the page's query cell (`SettingsSearchModel.Query`: the page host re-ranks
// on every change, never per frame), wrapped by `Search.Box(frame)`, which keys it `settings:search`, takes the focus tickets (Ctrl+F on Settings, Ctrl+,) and
// routes Down/Up to the model's highlight. Enter opens the highlighted result (the first when none is) through the model (`Open`: the deep-link door), which
// empties the box. Right-aligned beside the title, `ClassicSettingsRules.SearchWidth` wide; the row's width under the title at Compact. One tree at every tier:
// the box grows into its frame, whose width is a live prop, so a crossing moves the same box (its text, caret and popup).
//
// THE POPUP lists the top eight as glyph + title + breadcrumb (the model's `Place`), the highlighted one tinted, then "All results for …" (the body is the
// results page already: it closes the list). No match: the model's sentence and "Search the whole app for …".
//
// THE RESULTS PAGE replaces the body while the box holds a query: "Results for "x"" (BodyStrong), then each result as its breadcrumb (12 DIP secondary) over the
// dial's REAL row (the same card the category draws, live and writable: the result IS the row), keyed `settings:result:<id>`. A result whose parent is a
// result too rides its parent's expander instead of drawing a second control. A feature pointer is a clickable card to its place. No match: the model's
// sentence, its did-you-mean words (each sets the box) and the whole-app search.

using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Classic;

internal static partial class ClassicSettings
{
    // ══ 1. THE BOX ═════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The field's height follows the tier on the SAME box: the record's hit target where it asks for one (Compact's 40: the field, and the query
    /// glyph's plate grown out of its 32 x 28 slot to the target), the board's 32 elsewhere. Part modifiers re-read on every render of the box (a crossing
    /// re-measures its width, so it re-renders), never a remount.</summary>
    static readonly TemplateParts s_searchParts = new()
    {
        [AutoSuggestBox.PartRoot] = static r => s_layout.Touch
            ? r with { Height = s_layout.Target, MinHeight = s_layout.Target, MaxHeight = s_layout.Target }
            : r,
        [AutoSuggestBox.PartQueryButton] = static b => s_layout.Touch
            ? b with
            {
                Margin = new Edges4((AutoSuggestBox.QueryButtonWidth - s_layout.Target) / 2f, (AutoSuggestBox.QueryButtonHeight - s_layout.Target) / 2f,
                    (AutoSuggestBox.QueryButtonWidth - s_layout.Target) / 2f, (AutoSuggestBox.QueryButtonHeight - s_layout.Target) / 2f),
            }
            : b,
    };

    /// <summary>The masthead's box (see the file header).</summary>
    static Element SearchBox(SettingsSearchModel search, ClassicSettingsLayout layout)
    {
        var presenter = new AutoSuggestBoxPresenter(
            Build: context => Embed.Comp(new PopupProps(search, context.Width, context.Close), static () => new SuggestionPopup()),
            SubmitSelection: () => OpenHighlighted(search));
        bool under = layout.SearchUnder;
        var frame = new BoxEl
        {
            Direction = 1, MinWidth = 0f, Shrink = 0f,
            Width = under ? float.NaN : ClassicSettingsRules.SearchWidth, AlignSelf = under ? FlexAlign.Stretch : FlexAlign.Center,
            Animate = ClassicTierMotion.Reflow,
            Children =
            [
                AutoSuggestBox.Create(Array.Empty<string>(), Loc.Get(Strings.Theme.Classic.Settings.Find), grow: 1f, text: search.Query,
                    debounceMs: 0f, parts: s_searchParts, presenter: presenter, openOnSeed: false),
            ],
        };
        return search.Box(frame);
    }

    /// <summary>Enter in the box: the highlighted result (the first when none is) opens in its category; nothing to open leaves the box as it is.</summary>
    static bool OpenHighlighted(SettingsSearchModel search)
    {
        if (search.Count == 0) return false;
        int h = search.Highlight.Peek();
        search.Open(h >= 0 ? h : 0);
        return true;
    }

    sealed record PopupProps(SettingsSearchModel Search, IReadSignal<float> Width, Action Close);

    /// <summary>The suggestion popup (see the file header). Re-renders on a re-ranked query and on the highlight.</summary>
    sealed class SuggestionPopup : SettingsComponent
    {
        public override Element Render()
        {
            UseLayout();
            var p = UseProps<PopupProps>();
            var search = p.Search;
            _ = search.Version.Value;
            int highlight = search.Highlight.Value;
            float width = p.Width.Value;
            Action close = p.Close;
            // The box emptied without a keystroke (Escape through the model, a link that opened a dial, a strip click): the list goes with the query.
            bool active = search.Active;
            UseEffect(() => { if (!active) close(); }, DepKey.From(active));
            var rows = new List<Element>(ClassicSettingsRules.SuggestionRows + 2);
            if (search.NoMatch)
                rows.Add(new BoxEl
                {
                    Padding = ClassicSettingsRules.SuggestionRowPadding, MinHeight = ClassicSettingsRules.SuggestionFootHeight, Justify = FlexJustify.Center,
                    Direction = 1, MinWidth = 0f,
                    Children = [Body(search.NoMatchLine()) with { Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MinWidth = 0f }],
                });
            int count = Math.Min(search.Count, ClassicSettingsRules.SuggestionRows);
            for (int i = 0; i < count; i++)
            {
                SettingsHit hit = search.Hit(i);
                int at = i;
                rows.Add(SuggestionRow(hit, at == highlight, () =>
                {
                    close();
                    search.Open(at);
                }));
            }
            Element foot = search.NoMatch
                ? FootRow(search.SearchAppLine(), () =>
                {
                    close();
                    search.SearchApp();
                }, "settings:popup:app")
                : FootRow(Strings.Theme.Classic.Settings.AllResults(search.Ranked), close, "settings:popup:all");
            rows.Add(new BoxEl { Height = 1f, AlignSelf = FlexAlign.Stretch, Fill = Tok.StrokeDividerDefault });
            rows.Add(foot);
            return new BoxEl
            {
                Key = "settings:popup", Direction = 1, Width = width > 0f ? width : ClassicSettingsRules.SearchWidth, MinWidth = 0f,
                Padding = ClassicSettingsRules.SuggestionPopupPadding,
                Children = rows.ToArray(),
            };
        }

        static Element SuggestionRow(in SettingsHit hit, bool highlighted, Action open)
        {
            string glyph = hit.Dial is { } dial ? GlyphOf(dial) ?? Glyph(SettingsPageModel.GlyphOf(dial.Id)) : Icons.Forward;
            return new BoxEl
            {
                Key = "settings:popup:" + hit.Id, Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, MinWidth = 0f,
                MinHeight = MathF.Max(ClassicSettingsRules.SuggestionRowHeight, s_layout.Target), Padding = ClassicSettingsRules.SuggestionRowPadding,
                Corners = Radii.ControlAll, Fill = highlighted ? Tok.FillSubtleSecondary : ColorF.Transparent,
                HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary, Cursor = CursorId.Hand,
                Role = AutomationRole.Button, OnClick = open,
                Children =
                [
                    Icon(glyph, ClassicSettingsRules.SuggestionGlyph, Tok.TextPrimary) with { Shrink = 0f },
                    new BoxEl
                    {
                        Direction = 1, Gap = Spacing.XXS, Grow = 1f, Basis = 0f, MinWidth = 0f,
                        Children =
                        [
                            Body(hit.Title) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Wrap = TextWrap.NoWrap, MinWidth = 0f },
                            Caption(hit.Place) with { Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Wrap = TextWrap.NoWrap, MinWidth = 0f },
                        ],
                    },
                ],
            };
        }

        static Element FootRow(string text, Action onClick, string key) => new BoxEl
        {
            Key = key, Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f,
            MinHeight = MathF.Max(ClassicSettingsRules.SuggestionFootHeight, s_layout.Target), Padding = ClassicSettingsRules.SuggestionRowPadding,
            Corners = Radii.ControlAll, HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary, Cursor = CursorId.Hand,
            Role = AutomationRole.Button, OnClick = onClick,
            Children = [Body(text) with { Color = Tok.AccentTextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Wrap = TextWrap.NoWrap, MinWidth = 0f, Shrink = 1f }],
        };
    }

    // ══ 2. THE RESULTS PAGE ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The body while the box holds a query (see the file header).</summary>
    static Element ResultsBody(SettingsPageProps props)
    {
        var search = props.Search;
        var kids = new List<Element>(search.Count + 4)
        {
            new BoxEl
            {
                Key = "settings:results:title", Direction = 1, Gap = Spacing.XXS, MinWidth = 0f,
                Children = search.Close
                    ? [BodyStrong(Strings.Theme.Classic.Settings.ResultsFor(search.Ranked)) with { Wrap = TextWrap.Wrap, MinWidth = 0f },
                       Caption(search.CloseLine) with { Color = Tok.TextSecondary }]
                    : [BodyStrong(Strings.Theme.Classic.Settings.ResultsFor(search.Ranked)) with { Wrap = TextWrap.Wrap, MinWidth = 0f }],
            },
        };
        if (search.NoMatch) kids.Add(NoMatch(search));
        for (int i = 0; i < search.Count; i++)
        {
            SettingsHit hit = search.Hit(i);
            if (hit.Under >= 0) continue;   // its parent's expander holds it: one control per dial on the page
            if (ResultRow(props, hit, i) is { } row) kids.Add(row);
        }
        return new BoxEl { Key = "settings:results", Direction = 1, Gap = ClassicSettingsRules.ResultGap, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, Children = kids.ToArray() };
    }

    /// <summary>One result: its breadcrumb over the dial's real row, or a pointer's clickable card.</summary>
    static Element? ResultRow(SettingsPageProps props, in SettingsHit hit, int index)
    {
        Element? card;
        if (hit.Dial is { } dial)
        {
            if (props.Body.Row(dial) is not { } row) return null;
            if (!TryCustom(row, null, props, out card)) card = GenericCard(row);
            if (card is null) return null;
        }
        else
        {
            var search = props.Search;
            int at = index;
            card = Row(hit.Title, hit.Place.Length > 0 ? hit.Place : null, null, Icons.Forward, isClickEnabled: true, onClick: () => search.Open(at));
        }
        return new BoxEl
        {
            Key = SettingsKeys.Result(hit.Id), Direction = 1, Gap = ClassicSettingsRules.BreadcrumbGap, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
            Children =
            [
                Caption(hit.Dial is null ? hit.Title : hit.Place) with { Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Wrap = TextWrap.NoWrap, MinWidth = 0f },
                card.Value,
            ],
        };
    }

    /// <summary>No setting matched: the model's sentence, up to three "Did you mean" words (each puts itself in the box), and the whole-app search.</summary>
    static Element NoMatch(SettingsSearchModel search)
    {
        var words = new List<Element>(search.DidYouMeanCount + 1);
        if (search.DidYouMeanCount > 0)
        {
            words.Add(Caption(search.DidYouMeanLabel) with { Color = Tok.TextSecondary, Shrink = 0f });
            for (int i = 0; i < search.DidYouMeanCount; i++)
            {
                string word = search.DidYouMean(i);
                words.Add(LinkButton(word, () => search.Query.Value = word) with { Key = "settings:didyoumean:" + word });
            }
        }
        var kids = new List<Element>(3) { Body(search.NoMatchLine()) with { Wrap = TextWrap.Wrap, MinWidth = 0f } };
        if (words.Count > 0) kids.Add(new BoxEl { Direction = 0, Wrap = true, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinWidth = 0f, Children = words.ToArray() });
        kids.Add(LinkButton(search.SearchAppLine(), search.SearchApp) with { Key = "settings:searchapp" });
        return new BoxEl { Key = "settings:nomatch", Direction = 1, Gap = Spacing.S, AlignItems = FlexAlign.Start, MinWidth = 0f, Children = kids.ToArray() };
    }
}
