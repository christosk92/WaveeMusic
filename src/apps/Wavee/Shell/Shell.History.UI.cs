// ── Shell/Shell.History.UI.cs ──────────────────────────────────────────────────────────────────────────────────────
// the history page
//
// Role: UI
// Owner: I (stage B, I2)
// Wave: 4
// Budget: 400 lines
// Spec: ch 16 §0 item 15, §1.2, §2 W17-W21 + W25, §3.2, §3.3, §5 rows 27-28, §6.2, §9.1 item 14
//
// The navigation log as a page: a header (clock · title · two stat pills · Clear all), a search box, the filter chips
// and the sort combo, then either date-grouped cards (Most recent) or one deduplicated card (Most visited). EVERY
// decision it renders — the kinds, the filter, the search, the visible list, the most-visited fold, the date buckets,
// the timestamps, the unique count — is `Shell.cs` §11 (`Shell.History`), and the store is `Shell.Host.cs`. This file
// only lays them out.
//
// What History deliberately does NOT have (§6.2 — keep it that way): no context menu, no drag source, no ItemsView
// (a plain scroll over built rows; the log is capped at 500), no ScrollKey (returning starts at the top), no skeleton
// (the whole page is synchronous over an in-memory list), and no undo (Clear all asks first; a row delete does not).

using System;

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;

namespace Wavee;

public static partial class Shell
{
    // MOUNT POINT (stage B contract)
    /// <summary>The `history` route's page. Registered by the frame through <see cref="SetPage"/>; it reads the
    /// store's version signal itself and takes no route data.</summary>
    public static Element HistoryPage() => Embed.Comp(static () => new HistoryPageView());

    const float HistoryRowH = 56f, HistoryIconBox = 36f, HistoryDeleteBox = 28f;

    sealed class HistoryPageView : Component
    {
        public override Element Render()
        {
            var search = UseSignal("");
            var filterIndex = UseSignal(0);
            var sortIndex = UseSignal(0);
            var labels = UseRef<(string[] Filters, string[] Sorts)?>(null);
            var overlay = UseContext(Overlay.Service);

            // Built once per mount: a fresh array per render would re-push the chip bar's props every keystroke.
            labels.Value ??= (
                new[]
                {
                    Loc.Get(Strings.Nav.History.Filter.All), Loc.Get(Strings.Nav.History.Filter.Playlists),
                    Loc.Get(Strings.Nav.History.Filter.Shows), Loc.Get(Strings.Nav.History.Filter.Library),
                    Loc.Get(Strings.Nav.History.Filter.Search), Loc.Get(Strings.Nav.History.Filter.Pages),
                },
                new[] { Loc.Get(Strings.Nav.History.Sort.MostRecent), Loc.Get(Strings.Nav.History.Sort.MostVisited) });

            _ = History.Store.Version.Value;   // re-render when the log changes
            string query = search.Value;
            var filter = (History.Filter)Math.Clamp(filterIndex.Value, 0, (int)History.Filter.Pages);
            var sort = sortIndex.Value == 0 ? History.Sort.MostRecent : History.Sort.MostVisited;
            var entries = History.Store.Entries;
            var now = DateTime.Now;
            bool developerMode = Platform.Settings.Get(Platform.Keys.DeveloperMode);

            var visible = History.Visible(entries, filter, query);
            Element body;
            if (visible.Count == 0)
                body = HistoryEmpty(query, filter);
            else if (sort == History.Sort.MostVisited)
            {
                var counts = History.VisitCounts(entries);   // over the FULL unfiltered log
                body = HistoryMostVisited(History.MostVisited(visible, counts), counts, now, developerMode);
            }
            else
                body = HistoryDateGroups(visible, now, developerMode);

            var (filters, sorts) = labels.Value.Value;
            return new BoxEl
            {
                Grow = 1f, Direction = 1,
                Children =
                [
                    HistoryHeader(overlay, search, filterIndex, sortIndex, filters, sorts, entries.Count,
                        History.CountUniqueRoutes(entries)),
                    FluentGpu.Dsl.Ui.ScrollView(new BoxEl
                    {
                        Direction = 1, Gap = Spacing.L,
                        Padding = new Edges4(Spacing.PageWide, Spacing.M, Spacing.PageWide, Spacing.XXL),
                        Children = [body],
                    }) with { Grow = 1f },
                ],
            };
        }
    }

    // ── the header ──────────────────────────────────────────────────────────────────────────────────────────────────

    static Element HistoryHeader(IOverlayService overlay, FluentGpu.Signals.Signal<string> search,
        FluentGpu.Signals.Signal<int> filterIndex, FluentGpu.Signals.Signal<int> sortIndex, string[] filters, string[] sorts,
        int totalVisits, int uniqueRoutes)
        => new BoxEl
        {
            Direction = 1, Gap = Spacing.M,
            // No fill: the header inherits the content ground (a fill here fought the light theme).
            Padding = new Edges4(Spacing.PageWide, Spacing.L, Spacing.PageWide, Spacing.M),
            Children =
            [
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M,
                    Children =
                    [
                        FluentGpu.Dsl.Ui.Icon(Icons.Clock, 22f, Tok.TextPrimary),
                        Design.Type.PageHero(Loc.Get(Strings.Nav.History.Title)) with { Grow = 1f },
                        new BoxEl
                        {
                            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center,
                            Children =
                            [
                                HistoryStatPill(FormatCache.Int(totalVisits), Loc.Get(Strings.Nav.History.Stat.Visits)),
                                HistoryStatPill(FormatCache.Int(uniqueRoutes), Loc.Get(Strings.Nav.History.Stat.Unique)),
                            ],
                        },
                        // Destructive and unrecoverable (Clear also DELETES the file), so it asks first; the default
                        // button is Cancel.
                        Button.Standard(Loc.Get(Strings.Nav.History.ClearAll), () => Controls.Confirm(
                            overlay,
                            Loc.Get(Strings.Nav.History.ClearAllConfirm),
                            Loc.Get(Strings.Nav.History.ClearAllConfirmBody),
                            Loc.Get(Strings.Nav.History.ClearAll),
                            static () => History.Store.Clear())),
                    ],
                },
                AutoSuggestBox.Create(
                    Array.Empty<string>(),
                    placeholder: Loc.Get(Strings.Nav.History.SearchPlaceholder),
                    grow: 1f,
                    text: search,
                    onQuerySubmitted: q => search.Value = q,
                    onChange: q => search.Value = q,
                    minHeight: 36f,
                    cornerRadius: Radii.Control),
                new BoxEl
                {
                    // The bottom margin clears the chip bar's selection pill from the first group header.
                    Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M,
                    Margin = new Edges4(0f, 0f, 0f, Spacing.S),
                    Children =
                    [
                        SelectorBar.Create(filters, filterIndex),
                        new BoxEl { Grow = 1f },
                        ComboBox.Create(sorts, sortIndex, width: 160f),
                    ],
                },
            ],
        };

    static BoxEl HistoryStatPill(string value, string label) => new()
    {
        Direction = 0, Gap = Spacing.XS, AlignItems = FlexAlign.Center,
        Padding = new Edges4(Spacing.S, Spacing.XS, Spacing.S, Spacing.XS), Corners = Radii.FullAll,
        Fill = Tok.FillSubtleSecondary,
        Children =
        [
            new TextEl(value) { Size = 12f, LineHeight = 16f, Weight = 600, Color = Tok.TextPrimary },
            new TextEl(label) { Size = 12f, LineHeight = 16f, Color = Tok.TextSecondary },
        ],
    };

    // ── the bodies ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Three copies, picked by what narrowed the page: a search, a filter, or nothing logged at all. The same
    /// vacancy grammar as every other empty surface — display headline, one caption, no glyph, no action.</summary>
    static Element HistoryEmpty(string query, History.Filter filter)
    {
        if (query.Length > 0)
            return Controls.Vacancy(Controls.VacancyVoice.NoMatch, Controls.VacancyScale.Page,
                Loc.Get(Strings.Nav.History.Empty.NoResults), Strings.Nav.History.Empty.NoMatch(query));
        if (filter != History.Filter.All)
            return Controls.Vacancy(Controls.VacancyVoice.NoMatch, Controls.VacancyScale.Page,
                Loc.Get(Strings.Nav.History.Empty.NothingHere), Loc.Get(Strings.Nav.History.Empty.TryAll));
        return Controls.Vacancy(Controls.VacancyVoice.Empty, Controls.VacancyScale.Page,
            Loc.Get(Strings.Nav.History.Empty.NoHistory), Loc.Get(Strings.Nav.History.Empty.StartNavigating));
    }

    /// <summary>Most recent: one card per date bucket (Today · Yesterday · This week · This month · Earlier), each with
    /// an eyebrow + rule + count-pill header. The LAST row of each card carries no divider (contrast Most visited).</summary>
    static Element HistoryDateGroups(System.Collections.Generic.List<HistoryEntry> visible, DateTime now, bool developerMode)
    {
        var sections = new System.Collections.Generic.List<Element>();
        int i = 0;
        while (i < visible.Count)
        {
            string label = History.DateGroupLabel(visible[i].VisitedAt, now);
            int start = i;
            while (i < visible.Count && History.DateGroupLabel(visible[i].VisitedAt, now) == label) i++;
            var rows = new Element[i - start];
            for (int k = 0; k < rows.Length; k++)
                rows[k] = HistoryRow(visible[start + k], now, developerMode, visitCount: 0, showDivider: k < rows.Length - 1);
            sections.Add(new BoxEl
            {
                Direction = 1, Gap = Spacing.S,
                Children =
                [
                    HistoryGroupHeader(label, Strings.Nav.History.VisitCount(rows.Length)),
                    HistoryCard(rows),
                ],
            });
        }
        return new BoxEl { Direction = 1, Gap = Spacing.L, Children = sections.ToArray() };
    }

    /// <summary>Most visited: ONE flat card, newest entry per route key, count descending (the stable fold), a
    /// <c>N×</c> badge wherever N &gt; 1 — and EVERY row keeps its divider, the last included (the card clips it).</summary>
    static Element HistoryMostVisited(System.Collections.Generic.List<HistoryEntry> rowsInOrder,
        System.Collections.Generic.Dictionary<string, int> counts, DateTime now, bool developerMode)
    {
        var rows = new Element[rowsInOrder.Count];
        for (int k = 0; k < rows.Length; k++)
        {
            var e = rowsInOrder[k];
            int visits = counts.TryGetValue(NameOf(e.Route), out int v) ? v : 1;
            rows[k] = HistoryRow(e, now, developerMode, visitCount: visits, showDivider: true);
        }
        return new BoxEl
        {
            Direction = 1, Gap = Spacing.S,
            // The eyebrow string is authored ALL-CAPS in the loc table; this header carries NO count pill.
            Children = [HistoryGroupHeader(Loc.Get(Strings.Nav.History.MostVisited), count: null), HistoryCard(rows)],
        };
    }

    static BoxEl HistoryGroupHeader(string label, string? count)
    {
        var kids = new System.Collections.Generic.List<Element>(3)
        {
            Design.Type.Eyebrow(label) with { Color = Tok.TextSecondary },
            new BoxEl { Grow = 1f, Height = 1f, Margin = new Edges4(4f, 0f, 4f, 0f), Fill = Tok.StrokeDividerDefault },
        };
        if (count is not null)
            kids.Add(new BoxEl
            {
                Padding = new Edges4(Spacing.S, Spacing.XXS, Spacing.S, Spacing.XXS), Corners = Radii.FullAll,
                Fill = Tok.FillSubtleSecondary,
                Children = [new TextEl(count) { Size = 12f, LineHeight = 16f, Color = Tok.TextSecondary }],
            });
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, Margin = new Edges4(0f, 0f, 0f, 4f),
            Children = kids.ToArray(),
        };
    }

    static BoxEl HistoryCard(Element[] rows) => new()
    {
        Direction = 1, Corners = CornerRadius4.All(Radii.Card),
        Fill = Tok.FillCardSecondary, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
        ClipToBounds = true, Children = rows,
    };

    /// <summary>One visit, 56 DIP. A route this build has no page for (retired, or developer-gated) stays in the log at
    /// 0.6 opacity and INERT — still a record of where the user went, no longer an offer to go back — while its delete
    /// button stays live.</summary>
    static Element HistoryRow(HistoryEntry e, DateTime now, bool developerMode, int visitCount, bool showDivider)
    {
        var (title, glyph) = Dest(e.Route);
        string kind = History.KindOf(e.Route);
        bool isPlaylist = kind == "playlist";
        bool reachable = IsKnown(e.Route, developerMode);
        string kindLabel = kind switch
        {
            "playlist" => Loc.Get(Strings.Nav.History.Kind.Playlist),
            "show" => Loc.Get(Strings.Nav.History.Kind.Show),
            "library" => Loc.Get(Strings.Nav.History.Kind.Library),
            "search" => Loc.Get(Strings.Nav.History.Kind.Search),
            _ => Loc.Get(Strings.Nav.History.Kind.Page),   // a browse route labels itself Page — there is no "Browse" kind label
        };

        var meta = new System.Collections.Generic.List<Element>(3)
        {
            new TextEl(kindLabel) { Size = 12f, LineHeight = 16f, Color = Tok.TextTertiary },
        };
        if (ArgOf(e.Route) is { Length: > 0 } arg)
        {
            meta.Add(new TextEl("·") { Size = 12f, LineHeight = 16f, Color = Tok.TextTertiary });
            meta.Add(new TextEl(arg) { Size = 12f, LineHeight = 16f, Color = Tok.TextTertiary, Trim = TextTrim.CharacterEllipsis, MaxLines = 1 });
        }

        var kids = new System.Collections.Generic.List<Element>(5)
        {
            new BoxEl
            {
                Width = HistoryIconBox, Height = HistoryIconBox, Corners = CornerRadius4.All(Radii.Control),
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                // The playlist row is the ONE tinted cell.
                Fill = isPlaylist ? Tok.AccentDefault with { A = 0.12f } : Tok.FillSubtleSecondary,
                Children = [FluentGpu.Dsl.Ui.Icon(glyph, 16f, isPlaylist ? Tok.AccentDefault : Tok.TextSecondary)],
            },
            new BoxEl
            {
                Direction = 1, Grow = 1f, Gap = 2f, MinWidth = 0f,
                Children =
                [
                    FluentGpu.Dsl.Ui.Body(title) with { Trim = TextTrim.CharacterEllipsis, MaxLines = 1 },
                    new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.XS, MinWidth = 0f, Children = meta.ToArray() },
                ],
            },
            new TextEl(History.FormatTimestamp(e.VisitedAt, now)) { Size = 12f, LineHeight = 16f, Color = Tok.TextTertiary },
        };
        if (visitCount > 1)
            kids.Add(new BoxEl
            {
                Padding = new Edges4(Spacing.S, Spacing.XS, Spacing.S, Spacing.XS), Corners = Radii.FullAll,
                Fill = Tok.FillSubtleSecondary,
                Children = [new TextEl(Strings.Nav.History.VisitMultiplier(visitCount)) { Size = 12f, LineHeight = 16f, Color = Tok.TextSecondary }],
            });
        kids.Add(new BoxEl
        {
            Width = HistoryDeleteBox, Height = HistoryDeleteBox, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(Radii.Control), Opacity = 0.5f,
            Role = AutomationRole.Button,
            OnClick = () => History.Store.Remove(e),
            Children = [FluentGpu.Dsl.Ui.Icon(Icons.Cancel, 12f, Tok.TextSecondary)],
        }.Interactive(Interaction.Subtle));

        var route = e.Route;
        Element row = new BoxEl
        {
            Direction = 0, Height = HistoryRowH, AlignItems = FlexAlign.Center,
            Gap = Spacing.M, Padding = new Edges4(Spacing.M, 0f, Spacing.S, 0f),
            Opacity = reachable ? 1f : 0.6f,
            OnClick = reachable ? () => GoTo(route) : null,
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
                new BoxEl { Height = 1f, Margin = new Edges4(Spacing.M + HistoryIconBox + Spacing.M, 0f, 0f, 0f), Fill = Tok.StrokeDividerDefault },
            ],
        };
    }
}
