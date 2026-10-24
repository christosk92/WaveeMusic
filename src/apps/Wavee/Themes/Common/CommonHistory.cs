// ── Themes/Common/CommonHistory.cs — the history page, one copy for every theme (`Pages.History`, themes plan 6.5, T11c) ──────────
//
// From `Shell.History.UI.cs`. The navigation log as a page: a header (clock · title · two stat pills · Clear all), a search box, the filter chips
// and the sort combo, then either date-grouped cards (Most recent) or one deduplicated card (Most visited). EVERY decision it renders — the
// kinds, the filter, the search, the visible list, the most-visited fold, the date buckets, the timestamps, the unique count — is `Shell.History`
// (core), reached through `HistoryPageProps.Project`; the store is `Shell.Host.cs`. This file only lays them out.
//
// The page's three UI states (the search text, the filter chip, the sort) are this view's own signals (they were the page component's), and the
// view re-renders on the log's version signal and on them, as the component did.
//
// TIERS (themes plan 3.2, TC10): the page reads `ctx.Tier()` into a `CommonHistoryLayout` (Rules\Tiers.cs). Compact is one column: the head stacks (title, then the stats
// and Clear all, then the full-width search, the filter chips as one scrolling line and the sort picker), a row carries its time and visit count in its meta line, and every
// target is 40 DIP; the head scrolls with the list at Compact and at Short height; every tier is one column capped at the page's max width (the Huge fill rule).
//
// What History deliberately does NOT have (§6.2 — keep it that way): no context menu, no drag source, no ItemsView (a plain scroll over built
// rows; the log is capped at 500), no ScrollKey (returning starts at the top), no skeleton (the whole page is synchronous over an in-memory
// list), and no undo (Clear all asks first; a row delete does not).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Common;

/// <summary>The history page.</summary>
internal static class CommonHistory
{
    /// <summary>The registered template of <see cref="ViewKeys.HistoryPage"/>.</summary>
    public static Element Template(HistoryPageProps props, in ViewContext ctx)
    {
        var hooks = ctx.Hooks;
        var search = hooks.UseSignal("");
        var filterIndex = hooks.UseSignal(0);
        var sortIndex = hooks.UseSignal(0);
        var labels = hooks.UseRef<(string[] Filters, string[] Sorts)?>(null);
        var overlay = hooks.UseContext(Overlay.Service);

        // Built once per mount: a fresh array per render would re-push the chip bar's props every keystroke.
        labels.Value ??= (
            new[]
            {
                Loc.Get(Strings.Nav.History.Filter.All), Loc.Get(Strings.Nav.History.Filter.Playlists),
                Loc.Get(Strings.Nav.History.Filter.Shows), Loc.Get(Strings.Nav.History.Filter.Library),
                Loc.Get(Strings.Nav.History.Filter.Search), Loc.Get(Strings.Nav.History.Filter.Pages),
            },
            new[] { Loc.Get(Strings.Nav.History.Sort.MostRecent), Loc.Get(Strings.Nav.History.Sort.MostVisited) });

        _ = props.Version.Value;   // re-render when the log changes
        var model = props.Project(search.Value, filterIndex.Value, sortIndex.Value);
        var layout = CommonHistoryLayout.For(ctx.Tier());

        Element body = model.Body switch
        {
            HistoryBodyKind.Empty => Empty(model),
            HistoryBodyKind.MostVisited => MostVisited(model.Sections, layout),
            _ => DateGroups(model.Sections, layout),
        };

        var (filters, sorts) = labels.Value.Value;
        Element head = Header(overlay, search, filterIndex, sortIndex, filters, sorts, model.TotalVisits, model.UniqueRoutes, layout);
        Element list = new BoxEl
        {
            Direction = 1, Gap = Spacing.L,
            Padding = new Edges4(layout.Gutter, Spacing.M, layout.Gutter, Spacing.XXL),
            Children = [body],
        };
        // The head scrolls with the list where it would eat the page (Compact, Short); otherwise it is pinned over the scroll.
        Element[] page = layout.HeaderScrolls
            ? [Ui.ScrollView(new BoxEl { Direction = 1, Children = [head, list] }) with { Grow = 1f }]
            : [head, Ui.ScrollView(list) with { Grow = 1f }];
        // A row holding ONE column: the column stops growing at the tier's max width and keeps the page's left edge (the Huge fill rule).
        return new BoxEl
        {
            Grow = 1f, Direction = 0, MinWidth = 0f, MinHeight = 0f,
            Children =
            [
                new BoxEl { Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, MinHeight = 0f, MaxWidth = layout.MaxWidth, Children = page },
            ],
        };
    }

    // ── the header ──────────────────────────────────────────────────────────────────────────────────────────────────

    static Element Header(IOverlayService? overlay, Signal<string> search, Signal<int> filterIndex, Signal<int> sortIndex,
        string[] filters, string[] sorts, int totalVisits, int uniqueRoutes, CommonHistoryLayout layout)
    {
        // Destructive and unrecoverable (Clear also DELETES the file), so it asks first; the default
        // button is Cancel.
        Element clearAll = CommonTouch.Button(Loc.Get(Strings.Nav.History.ClearAll), () => Controls.Confirm(
            overlay,
            Loc.Get(Strings.Nav.History.ClearAllConfirm),
            Loc.Get(Strings.Nav.History.ClearAllConfirmBody),
            Loc.Get(Strings.Nav.History.ClearAll),
            static () => HistoryVerbs.ClearAll()), layout.Target);
        Element stats = new BoxEl
        {
            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center,
            Children =
            [
                StatPill(FormatCache.Int(totalVisits), Loc.Get(Strings.Nav.History.Stat.Visits)),
                StatPill(FormatCache.Int(uniqueRoutes), Loc.Get(Strings.Nav.History.Stat.Unique)),
            ],
        };
        Element searchBox = AutoSuggestBox.Create(
            Array.Empty<string>(),
            placeholder: Loc.Get(Strings.Nav.History.SearchPlaceholder),
            grow: 1f,
            text: search,
            onQuerySubmitted: q => search.Value = q,
            onChange: q => search.Value = q,
            minHeight: layout.Target > 0f ? layout.Target : CommonHistoryRules.SearchMinHeight,
            cornerRadius: Radii.Control);

        Element[] top = layout.Compact
            ?
            [
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M,
                    Children =
                    [
                        Ui.Icon(Icons.Clock, CommonHistoryRules.TitleGlyphSize, Tok.TextPrimary),
                        R.Type.PageHero(Loc.Get(Strings.Nav.History.Title)) with { Grow = 1f, MinWidth = 0f, Wrap = TextWrap.Wrap, MaxLines = CommonHistoryRules.TitleLines, Trim = TextTrim.CharacterEllipsis },
                    ],
                },
                new BoxEl { Direction = 0, Wrap = true, Gap = Spacing.S, AlignItems = FlexAlign.Center, Children = [stats, clearAll] },
                searchBox,
                // The six chips are wider than the column: one scrolling line, its end fading where more wait.
                Ui.ScrollView(SelectorBar.Create(filters, filterIndex, style: new SelectorBarStyle { ItemHeight = layout.Target }), horizontal: true) with
                {
                    Grow = 0f, Shrink = 0f, MinWidth = 0f, Height = CommonHistoryRules.ChipRailHeight, SuppressScrollBar = true, AutoEdgeFade = true,
                },
                CommonTouch.Picker(sorts, sortIndex, CommonHistoryRules.SortComboWidth, layout.Target),
            ]
            :
            [
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M,
                    Children =
                    [
                        Ui.Icon(Icons.Clock, CommonHistoryRules.TitleGlyphSize, Tok.TextPrimary),
                        R.Type.PageHero(Loc.Get(Strings.Nav.History.Title)) with { Grow = 1f },
                        stats,
                        clearAll,
                    ],
                },
                searchBox,
                new BoxEl
                {
                    // The bottom margin clears the chip bar's selection pill from the first group header.
                    Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M,
                    Margin = new Edges4(0f, 0f, 0f, Spacing.S),
                    Children =
                    [
                        SelectorBar.Create(filters, filterIndex),
                        new BoxEl { Grow = 1f },
                        CommonTouch.Picker(sorts, sortIndex, CommonHistoryRules.SortComboWidth, layout.Target),
                    ],
                },
            ];
        return new BoxEl
        {
            Direction = 1, Gap = Spacing.M,
            // No fill: the header inherits the content ground (a fill here fought the light theme).
            Padding = new Edges4(layout.Gutter, Spacing.L, layout.Gutter, Spacing.M),
            Children = top,
        };
    }

    static BoxEl StatPill(string value, string label) => new()
    {
        Direction = 0, Gap = Spacing.XS, AlignItems = FlexAlign.Center,
        Padding = new Edges4(Spacing.S, Spacing.XS, Spacing.S, Spacing.XS), Corners = Radii.FullAll,
        Fill = Tok.FillSubtleSecondary,
        Children =
        [
            new TextEl(value) { Size = CommonHistoryRules.SmallTextSize, LineHeight = CommonHistoryRules.SmallTextLine, Weight = CommonHistoryRules.StatFigureWeight, Color = Tok.TextPrimary },
            new TextEl(label) { Size = CommonHistoryRules.SmallTextSize, LineHeight = CommonHistoryRules.SmallTextLine, Color = Tok.TextSecondary },
        ],
    };

    // ── the bodies ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Three copies, picked by what narrowed the page (the core chose the words): a search, a filter, or nothing logged at all. The same
    /// vacancy grammar as every other empty surface — display headline, one caption, no glyph, no action.</summary>
    static Element Empty(HistoryPageModel model)
        => Controls.Vacancy(model.EmptyIsNoMatch ? Controls.VacancyVoice.NoMatch : Controls.VacancyVoice.Empty, Controls.VacancyScale.Page,
            model.EmptyTitle, model.EmptyCaption);

    /// <summary>Most recent: one card per date bucket (Today · Yesterday · This week · This month · Earlier), each with
    /// an eyebrow + rule + count-pill header. The LAST row of each card carries no divider (contrast Most visited).</summary>
    static Element DateGroups(IReadOnlyList<HistorySection> sections, CommonHistoryLayout layout)
    {
        var cards = new Element[sections.Count];
        for (int s = 0; s < cards.Length; s++)
        {
            var section = sections[s];
            var rows = new Element[section.Rows.Count];
            for (int k = 0; k < rows.Length; k++)
                rows[k] = Row(section.Rows[k], showDivider: k < rows.Length - 1, layout);
            cards[s] = new BoxEl
            {
                Direction = 1, Gap = Spacing.S,
                Children =
                [
                    GroupHeader(section.Label, section.Count),
                    Card(rows),
                ],
            };
        }
        return new BoxEl { Direction = 1, Gap = Spacing.L, Children = cards };
    }

    /// <summary>Most visited: ONE flat card, newest entry per route key, count descending (the stable fold), a
    /// <c>N×</c> badge wherever N &gt; 1 — and EVERY row keeps its divider, the last included (the card clips it).</summary>
    static Element MostVisited(IReadOnlyList<HistorySection> sections, CommonHistoryLayout layout)
    {
        var section = sections[0];
        var rows = new Element[section.Rows.Count];
        for (int k = 0; k < rows.Length; k++)
            rows[k] = Row(section.Rows[k], showDivider: true, layout);
        return new BoxEl
        {
            Direction = 1, Gap = Spacing.S,
            // The eyebrow string is authored ALL-CAPS in the loc table; this header carries NO count pill.
            Children = [GroupHeader(section.Label, count: null), Card(rows)],
        };
    }

    static BoxEl GroupHeader(string label, string? count)
    {
        var kids = new List<Element>(3)
        {
            R.Type.Eyebrow(label) with { Color = Tok.TextSecondary },
            new BoxEl { Grow = 1f, Height = 1f, Margin = new Edges4(CommonHistoryRules.GroupRuleInset, 0f, CommonHistoryRules.GroupRuleInset, 0f), Fill = Tok.StrokeDividerDefault },
        };
        if (count is not null)
            kids.Add(new BoxEl
            {
                Padding = new Edges4(Spacing.S, Spacing.XXS, Spacing.S, Spacing.XXS), Corners = Radii.FullAll,
                Fill = Tok.FillSubtleSecondary,
                Children = [new TextEl(count) { Size = CommonHistoryRules.SmallTextSize, LineHeight = CommonHistoryRules.SmallTextLine, Color = Tok.TextSecondary }],
            });
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, Margin = new Edges4(0f, 0f, 0f, CommonHistoryRules.GroupRuleInset),
            Children = kids.ToArray(),
        };
    }

    static BoxEl Card(Element[] rows) => new()
    {
        Direction = 1, Corners = CornerRadius4.All(Radii.Card),
        Fill = Tok.FillCardSecondary, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
        ClipToBounds = true, Children = rows,
    };

    /// <summary>One visit, 56 DIP. A route this build has no page for (retired, or developer-gated) stays in the log at
    /// 0.6 opacity and INERT — still a record of where the user went, no longer an offer to go back — while its delete
    /// button stays live.</summary>
    static Element Row(in HistoryRowFacts e, bool showDivider, CommonHistoryLayout layout)
    {
        // Compact has no room for a time and a count beside the title: they join the meta line under it.
        bool compact = layout.Compact;
        var meta = new List<Element>(5)
        {
            new TextEl(e.KindLabel) { Size = CommonHistoryRules.SmallTextSize, LineHeight = CommonHistoryRules.SmallTextLine, Color = Tok.TextTertiary },
        };
        if (e.Arg is { Length: > 0 } arg)
        {
            meta.Add(new TextEl("·") { Size = CommonHistoryRules.SmallTextSize, LineHeight = CommonHistoryRules.SmallTextLine, Color = Tok.TextTertiary });
            meta.Add(new TextEl(arg) { Size = CommonHistoryRules.SmallTextSize, LineHeight = CommonHistoryRules.SmallTextLine, Color = Tok.TextTertiary, Trim = TextTrim.CharacterEllipsis, MaxLines = 1, Shrink = 1f, MinWidth = 0f });
        }
        if (compact)
        {
            string tail = e.VisitCount > 1 ? e.Timestamp + " · " + Strings.Nav.History.VisitMultiplier(e.VisitCount) : e.Timestamp;
            meta.Add(new TextEl("·") { Size = CommonHistoryRules.SmallTextSize, LineHeight = CommonHistoryRules.SmallTextLine, Color = Tok.TextTertiary });
            meta.Add(new TextEl(tail) { Size = CommonHistoryRules.SmallTextSize, LineHeight = CommonHistoryRules.SmallTextLine, Color = Tok.TextTertiary, Shrink = 0f });
        }

        var kids = new List<Element>(5)
        {
            new BoxEl
            {
                Width = CommonHistoryRules.IconBox, Height = CommonHistoryRules.IconBox, Corners = CornerRadius4.All(Radii.Control),
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                // The playlist row is the ONE tinted cell.
                Fill = e.IsPlaylist ? Tok.AccentDefault with { A = CommonHistoryRules.PlaylistTintAlpha } : Tok.FillSubtleSecondary,
                Children = [Ui.Icon(e.Glyph, CommonHistoryRules.RowGlyphSize, e.IsPlaylist ? Tok.AccentDefault : Tok.TextSecondary)],
            },
            new BoxEl
            {
                Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, Gap = CommonHistoryRules.TitleMetaGap, MinWidth = 0f,
                Children =
                [
                    Ui.Body(e.Title) with { Trim = TextTrim.CharacterEllipsis, MaxLines = 1 },
                    new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.XS, MinWidth = 0f, Children = meta.ToArray() },
                ],
            },
        };
        if (!compact)
        {
            kids.Add(new TextEl(e.Timestamp) { Size = CommonHistoryRules.SmallTextSize, LineHeight = CommonHistoryRules.SmallTextLine, Color = Tok.TextTertiary });
            if (e.VisitCount > 1)
                kids.Add(new BoxEl
                {
                    Padding = new Edges4(Spacing.S, Spacing.XS, Spacing.S, Spacing.XS), Corners = Radii.FullAll,
                    Fill = Tok.FillSubtleSecondary,
                    Children = [new TextEl(Strings.Nav.History.VisitMultiplier(e.VisitCount)) { Size = CommonHistoryRules.SmallTextSize, LineHeight = CommonHistoryRules.SmallTextLine, Color = Tok.TextSecondary }],
                });
        }
        float deleteBox = layout.Target > 0f ? layout.Target : CommonHistoryRules.DeleteBox;
        kids.Add(new BoxEl
        {
            Width = deleteBox, Height = deleteBox, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Shrink = 0f,
            Corners = CornerRadius4.All(Radii.Control), Opacity = CommonHistoryRules.DeleteRestOpacity,
            Role = AutomationRole.Button,
            OnClick = e.Remove,
            Children = [Ui.Icon(Icons.Cancel, CommonHistoryRules.DeleteGlyphSize, Tok.TextSecondary)],
        }.Interactive(Interaction.Subtle));

        bool reachable = e.Reachable;
        Element row = new BoxEl
        {
            Direction = 0, Height = CommonHistoryRules.RowHeight, AlignItems = FlexAlign.Center,
            Gap = Spacing.M, Padding = new Edges4(Spacing.M, 0f, Spacing.S, 0f),
            Opacity = reachable ? 1f : CommonHistoryRules.InertRowOpacity,
            OnClick = reachable ? e.Open : null,
            Children = kids.ToArray(),
            // isEnabled goes THROUGH Interactive: the recipe writes IsEnabled itself and would silently overwrite an
            // initializer value.
        }.Interactive(Interaction.Subtle, isEnabled: reachable);

        if (!showDivider) return row;
        return new BoxEl
        {
            Direction = 1,
            Children =
            [
                row,
                // Inset past the icon column (12 + 36 + 12): the card reads as a list, not a table.
                new BoxEl { Height = 1f, Margin = new Edges4(Spacing.M + CommonHistoryRules.IconBox + Spacing.M, 0f, 0f, 0f), Fill = Tok.StrokeDividerDefault },
            ],
        };
    }
}
