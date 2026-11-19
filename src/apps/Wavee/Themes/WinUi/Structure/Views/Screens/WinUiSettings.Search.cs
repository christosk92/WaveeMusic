// ── Themes/WinUi/Structure/Views/Screens/WinUiSettings.Search.cs — "Find a setting": the box, its suggestion list, the results page (design 4.8, 7.2) ──────
//
// Windows Settings' two steps, over the core's search model (`SettingsPageProps.Search`: the query cell, the ranking into its own buffers, the highlight,
// the no-match words, the focus tickets):
//   1. TYPING opens the box's suggestion list (the AutoSuggestBox's rich presenter): the top eight results as glyph (the category's) + title over the
//      breadcrumb ("Playback › Between songs"), each keyed `settings:suggest:<id>`. Down/Up move the model's highlight through them, Enter or a click on
//      one deep-links to its dial (the model's `Open`: the category opens, the parent expander, the glide, the highlight). No match: "No results".
//   2. ENTER with nothing highlighted (or the query button) opens the RESULTS PAGE in place of the body: "Search results for "x"" (Subtitle) and, for each
//      result, its breadcrumb (12, secondary, a link to the dial in its category) over the dial's REAL row (the same card the category draws, live and
//      writable: the result IS the row), keyed `settings:result:<id>`. A child whose parent is a result rides its parent's expander, opened. No match: the
//      page says "No settings match "x"." with the "Did you mean" words (each runs that word) and "Search the whole app for "x"".
// The box is bound through the model (`Search.Box(frame)` keys the frame `settings:search`, takes the Ctrl+F / Ctrl+, focus tickets); the AutoSuggestBox owns
// the field's own keys, so the list's keys reach the model through the presenter's hooks. A keystroke allocates only what the box writes; the list and the
// page re-render on the model's version, never per frame.

using System.Globalization;
using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.WinUi;

internal static partial class WinUiSettings
{
    // ══ 1. THE BOX ════════════════════════════════════════════════════════════════════════════════════════════════

    sealed record SearchFieldProps(SettingsSearchModel Model);

    /// <summary>The model's keys as the list's hooks press them (the box's own field keeps Enter and Escape: see the file header).</summary>
    static readonly KeyEventArgs s_keyDown = new(Keys.Down), s_keyUp = new(Keys.Up);

    /// <summary>The stock AutoSuggestBox (32 tall, the control radius, the text-box chrome) filling its frame: the frame's width is the head's choice (360 beside
    /// the title, the column under it). The text IS the model's query cell.</summary>
    sealed class SearchField : SettingsComponent
    {
        public override Element Render()
        {
            var layout = UseLayout();
            var model = UseProps<SearchFieldProps>().Model;
            var results = s_resultsState.Value;
            // The field is the tier's hit target tall where it asks for one (Compact: 40, so the query button reaches the floor), 32 elsewhere. The box reads
            // its height once, at mount; the root's part reads this signal instead, so a crossing re-sizes the SAME field (its text, caret, open list).
            bool touch = layout.Touch;
            var tall = UseSignal(touch);
            UseEffect(() => tall.SetIfChanged(touch), DepKey.From(touch ? 1 : 0));
            var parts = UseMemo(() =>
            {
                var p = new TemplateParts();
                p[AutoSuggestBox.PartQueryButton] = static b => b with { HitSlop = WinUiSettingsRules.SearchQueryHitSlop };
                p[AutoSuggestBox.PartRoot] = b =>
                {
                    float h = tall.Value ? WinUiSettingsRules.SearchTouchH : WinUiSettingsRules.SearchH;
                    return b with { Height = h, MinHeight = h, MaxHeight = h };
                };
                return p;
            }, DepKey.Empty);
            var presenter = UseMemo(() => new AutoSuggestBoxPresenter(
                Build: context => Embed.Comp(new SuggestProps(model, context.Width, context.Close), static () => new SuggestList()),
                MoveSelection: delta => MoveHighlight(model, delta),
                SubmitSelection: () =>
                {
                    int highlighted = model.Highlight.Peek();
                    if (highlighted < 0) return false;   // nothing highlighted: Enter submits the query (the results page)
                    model.Open(highlighted);
                    return true;
                },
                ResetSelection: () => ResetHighlight(model)), DepKey.Empty);
            return AutoSuggestBox.Create(Array.Empty<string>(), Loc.Bind(Strings.Theme.Winui.Screens.Settings.Find), grow: 1f, text: model.Query,
                onQuerySubmitted: _ => { if (model.Active) results.Value = true; },
                minHeight: WinUiSettingsRules.SearchH, presenter: presenter, parts: parts, openOnSeed: false);
        }
    }

    /// <summary>Down / Up through the list's rows (never past the eighth: the list shows no more).</summary>
    static void MoveHighlight(SettingsSearchModel model, int delta)
    {
        int shown = Math.Min(model.Count, WinUiSettingsRules.SuggestionRows);
        if (shown == 0) return;
        if (delta > 0 && model.Highlight.Peek() >= shown - 1) return;
        model.PreviewKey(delta > 0 ? s_keyDown : s_keyUp);
    }

    /// <summary>The list closed or the text changed: nothing highlighted.</summary>
    static void ResetHighlight(SettingsSearchModel model)
    {
        for (int guard = model.Count; guard >= 0 && model.Highlight.Peek() >= 0; guard--) model.PreviewKey(s_keyUp);
    }

    // ══ 2. THE SUGGESTION LIST ════════════════════════════════════════════════════════════════════════════════════

    sealed record SuggestProps(SettingsSearchModel Model, IReadSignal<float> Width, Action Close);

    /// <summary>The list's body, rendered by the model's version and highlight (the popup's content thunk is a one-shot otherwise).</summary>
    sealed class SuggestList : SettingsComponent
    {
        public override Element Render()
        {
            UseLayout();
            var p = UseProps<SuggestProps>();
            var model = p.Model;
            int version = model.Version.Value;
            int highlighted = model.Highlight.Value;
            float w = p.Width.Value;
            int shown = Math.Min(model.Count, WinUiSettingsRules.SuggestionRows);
            // The box emptied without a keystroke (Escape, a link that opened a dial, the model's own clear): the list goes with the query. A keystroke
            // never empties the cell while the list is open (backspacing to nothing closes it in the box itself), so a fresh list is never closed here.
            bool cleared = model.Query.Peek().Length == 0 && !model.Active;
            Action close = p.Close;
            UseEffect(() => { if (cleared) close(); }, DepKey.From(version));

            if (shown == 0)
                return new BoxEl
                {
                    Key = "settings:suggest:none", Direction = 0, Width = w, MinWidth = w, MinHeight = AutoSuggestBox.ItemMinHeight, AlignItems = FlexAlign.Center,
                    Padding = WinUiSettingsRules.SuggestionPadding,
                    Children = [Body(Loc.Get(Strings.Theme.Winui.Screens.Settings.NoResults)) with { Color = Tok.TextSecondary, MaxLines = 1, Wrap = TextWrap.NoWrap }],
                };

            var rows = new Element[shown];
            for (int i = 0; i < shown; i++)
            {
                var hit = model.Hit(i);
                int at = i;
                rows[i] = new BoxEl
                {
                    Key = "settings:suggest:" + hit.Id, Direction = 0, AlignItems = FlexAlign.Center, Gap = WinUiSettingsRules.SuggestionGlyphGap,
                    MinHeight = WinUiSettingsRules.SuggestionRowH, Margin = WinUiSettingsRules.SuggestionInset, Padding = WinUiSettingsRules.SuggestionPadding,
                    Corners = Radii.ControlAll, MinWidth = 0f,
                    Fill = at == highlighted ? Tok.FillSubtleSecondary : Tok.FillSubtleTransparent,
                    HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
                    Role = AutomationRole.MenuItem, Cursor = CursorId.Hand,
                    OnClick = () =>
                    {
                        model.Open(at);
                        close();
                    },
                    Children =
                    [
                        Icon(Glyph(GlyphOfHit(hit)), WinUiSettingsRules.SuggestionGlyph, Tok.TextSecondary) with { Shrink = 0f },
                        new BoxEl
                        {
                            Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f,
                            Children =
                            [
                                Body(hit.Title) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Wrap = TextWrap.NoWrap, MinWidth = 0f },
                                Caption(hit.Place) with { Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Wrap = TextWrap.NoWrap, MinWidth = 0f },
                            ],
                        },
                    ],
                };
            }
            return new ScrollEl
            {
                Width = w, MinWidth = w, MaxHeight = AutoSuggestBox.MaxPopupHeight, ContentSized = true,
                Content = new BoxEl { Direction = 1, Width = w, MinWidth = w, Children = rows },
            };
        }
    }

    /// <summary>A result's glyph: its category's (a pointer's place has none of its own: the gear).</summary>
    static string? GlyphOfHit(in SettingsHit hit) => hit.Dial is { } dial ? SettingsPageModel.GlyphOf(dial.Id) : null;

    // ══ 3. THE RESULTS PAGE ═══════════════════════════════════════════════════════════════════════════════════════

    sealed record ResultsProps(SettingsPageProps Page);

    /// <summary>"Search results for "x"" over the real rows (see the file header). Re-renders on the model's version: a word typed after Enter re-ranks the page.</summary>
    sealed class ResultsPage : SettingsComponent
    {
        public override Element Render()
        {
            UseLayout();
            var page = UseProps<ResultsProps>().Page;
            var model = page.Search;
            _ = model.Version.Value;
            string query = model.Ranked.Trim();

            var kids = new List<Element>(model.Count + 3)
            {
                new TextEl(Strings.Theme.Winui.Screens.Settings.Results(query))
                {
                    Key = "settings:results:title", Size = WinUiSettingsRules.ResultsTitleSize, LineHeight = WinUiSettingsRules.ResultsTitleLine, Weight = 600,
                    Color = Tok.TextPrimary, Wrap = TextWrap.Wrap, MinWidth = 0f,
                },
            };
            if (model.NoMatch)
            {
                kids.Add(NoMatchCard(model));
                return ResultsColumn(kids);
            }

            var cards = new List<Element>(model.Count);
            for (int i = 0; i < model.Count; i++)
            {
                var hit = model.Hit(i);
                if (hit.Under >= 0) continue;   // it rides its parent's result, whose expander opens for it
                if (ResultOf(page, model, i, hit) is { } card) cards.Add(card);
            }
            string count = cards.Count == 1
                ? Loc.Get(Strings.Theme.Winui.Screens.Settings.ResultsCountOne)
                : Strings.Theme.Winui.Screens.Settings.ResultsCount(cards.Count.ToString(CultureInfo.CurrentCulture));
            kids.Add(Caption(model.Close ? model.CloseLine + " · " + count : count) with
            {
                Key = "settings:results:count", Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MinWidth = 0f, Margin = new Edges4(0f, WinUiSettingsRules.ResultsHeadGap, 0f, 0f),
            });
            kids.Add(new BoxEl
            {
                Key = "settings:results:list", Direction = 1, Gap = WinUiSettingsRules.ResultGap, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
                Margin = new Edges4(0f, WinUiSettingsRules.ResultsListGap, 0f, 0f),
                Children = cards.ToArray(),
            });
            return ResultsColumn(kids);
        }

        static Element ResultsColumn(List<Element> kids)
            => new BoxEl { Key = ColumnKey, Direction = 1, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, Children = kids.ToArray() };
    }

    /// <summary>One result: its breadcrumb (a link to the dial in its category) over the dial's real row; a feature pointer is a door to its place.</summary>
    static Element? ResultOf(SettingsPageProps page, SettingsSearchModel model, int index, in SettingsHit hit)
    {
        string key = SettingsKeys.Result(hit.Id);
        if (hit.Pointer is not null)
        {
            var door = NewCard(hit.Title, hit.Place.Length > 0 ? hit.Place : null) with { OnClick = () => model.Open(index) };
            return new BoxEl { Key = key, Direction = 1, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, Children = [DialCard(door)] };
        }
        if (hit.Dial is not { } dial || page.Body.Row(dial) is not { } row) return null;

        bool openChildren = false;
        for (int j = index + 1; j < model.Count && !openChildren; j++) openChildren = model.Hit(j).Under == index;
        SettingsCategory category = SettingsSpec.TryPlace(dial, out var c, out _) ? c : SettingsSpec.Categories[0];
        var body = new SettingsBody(SettingsPageModel.Categories[SettingsPageModel.IndexOf(category)], []);
        var host = Embed.Comp(new DialRowProps(row, body, false, page.Focus, page, null) { Open = openChildren }, static () => new DialRow())
            with { Key = SettingsKeys.Dial(row.Id) };
        int at = index;
        return new BoxEl
        {
            Key = key, Direction = 1, Gap = WinUiSettingsRules.ResultBreadcrumbGap, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
            Children =
            [
                new BoxEl
                {
                    Direction = 0, AlignSelf = FlexAlign.Start, MinWidth = 0f, Cursor = CursorId.Hand, Role = AutomationRole.Hyperlink, Focusable = true,
                    HitSlop = s_layout.Touch ? WinUiSettingsRules.BreadcrumbTouchSlop : default,
                    OnClick = () => model.Open(at),
                    Children = [Caption(hit.Place) with { Color = Tok.TextSecondary, HoverColor = Tok.TextPrimary, Wrap = TextWrap.Wrap, MinWidth = 0f }],
                },
                host,
            ],
        };
    }

    /// <summary>The no-match card: "No settings match "x"." with up to three "Did you mean" words (each runs that word) and the whole app's search.</summary>
    static Element NoMatchCard(SettingsSearchModel model)
    {
        var words = new List<Element>(model.DidYouMeanCount * 2 + 1);
        if (model.DidYouMeanCount > 0)
        {
            words.Add(Body(model.DidYouMeanLabel) with { Color = Tok.TextSecondary, Shrink = 0f });
            for (int i = 0; i < model.DidYouMeanCount; i++)
            {
                string word = model.DidYouMean(i);
                words.Add(LinkButton(word, () => model.Query.Value = word) with { Key = "settings:didyoumean:" + word });
            }
        }
        var kids = new List<Element>(3)
        {
            Body(model.NoMatchLine()) with { Wrap = TextWrap.Wrap, MinWidth = 0f },
        };
        if (words.Count > 0)
            kids.Add(new BoxEl { Direction = 0, Wrap = true, AlignItems = FlexAlign.Center, Gap = WinUiSettingsRules.NoMatchGap, MinWidth = 0f, Children = words.ToArray() });
        kids.Add(LinkButton(model.SearchAppLine(), model.SearchApp) with { Key = "settings:searchapp" });
        var card = NewCard("", null, new BoxEl { Direction = 1, Gap = WinUiSettingsRules.NoMatchGap, MinWidth = 0f, Children = kids.ToArray() }) with { Form = CardForm.Left };
        return new BoxEl
        {
            Key = "settings:nomatch", Direction = 1, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, Margin = new Edges4(0f, WinUiSettingsRules.ResultsListGap, 0f, 0f),
            Children = [DialCard(card)],
        };
    }
}
