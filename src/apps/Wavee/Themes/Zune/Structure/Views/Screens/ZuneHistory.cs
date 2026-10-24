// ── Themes/Zune/Structure/Views/Screens/ZuneHistory.cs — Zune's history page (`Pages.History`; audit B5a, V20) ──────────────────────────────
//
// The Common page (`Themes\Common\CommonHistory.cs`) drew History in no theme's voice under Zune: a clock glyph beside a small Title Case title,
// stat pills ("9 Visits"), chip plates, bordered cards. No board draws History (owner Q18), so it takes the voice of Zune's utility screens
// (Z-Settings-*): the panorama title "history" bled to the gutter, then the meta words ("9 visits · 9 unique") with "clear all" as a word at the
// end, the find field and the two word rails (all · playlists · podcasts · library · search · pages, and most recent · most visited, under the
// accent underline), then the day groups: a 22 light head with its count, and flat rows (the glyph, the title over its kind line, the time, the
// visit count, the remove glyph) that answer hover with the 5 % wash. The title stands over the scroll, as Settings' does.
//
// EVERY decision is the core's (`HistoryPageProps.Project`: the kinds, the filter, the search, the fold, the buckets, the timestamps, the unique
// count) and the verbs are `Shell\HistoryVerbs.cs`; this file only lays them out. Numbers: `Rules\ZuneUtilityRules.cs`; the title zone and the gutter
// are the settings record's (`ZuneSettingsTiers`), the family the screen belongs to.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;
using K = Wavee.Themes.Zune.ZuneDiscoveryKit;
using U = Wavee.Themes.Zune.ZuneUtilityRules;

namespace Wavee.Themes.Zune;

/// <summary>Zune's history page.</summary>
internal static class ZuneHistory
{
    /// <summary>The page title's node.</summary>
    internal const string TitleKey = "zh:title";

    /// <summary>The registered template of <see cref="ViewKeys.HistoryPage"/>.</summary>
    public static Element Template(HistoryPageProps props, in ViewContext ctx)
    {
        var hooks = ctx.Hooks;
        var search = hooks.UseSignal("");
        var filterIndex = hooks.UseSignal(0);
        var sortIndex = hooks.UseSignal(0);
        // The rails' words are frozen at mount (a different set is a remount): built once.
        var rails = hooks.UseRef<(Controls.Words.Word[] Filters, Controls.Words.Word[] Sorts)?>(null);
        var overlay = hooks.UseContext(Overlay.Service);
        rails.Value ??= (
            [
                new(Loc.Bind(Strings.Nav.History.Filter.All)), new(Loc.Bind(Strings.Nav.History.Filter.Playlists)),
                new(Loc.Bind(Strings.Nav.History.Filter.Shows)), new(Loc.Bind(Strings.Nav.History.Filter.Library)),
                new(Loc.Bind(Strings.Nav.History.Filter.Search)), new(Loc.Bind(Strings.Nav.History.Filter.Pages)),
            ],
            [new(Loc.Bind(Strings.Nav.History.Sort.MostRecent)), new(Loc.Bind(Strings.Nav.History.Sort.MostVisited))]);

        _ = props.Version.Value;   // re-render when the log changes
        var model = props.Project(search.Value, filterIndex.Value, sortIndex.Value);
        var lay = ZuneSettingsTiers.Of(ctx.Tier());
        var (filters, sorts) = rails.Value.Value;

        Element body = model.Body switch
        {
            HistoryBodyKind.Empty => Controls.Vacancy(model.EmptyIsNoMatch ? Controls.VacancyVoice.NoMatch : Controls.VacancyVoice.Empty, Controls.VacancyScale.Page,
                model.EmptyTitle, model.EmptyCaption),
            _ => Groups(model.Sections, lay),
        };

        var column = new BoxEl
        {
            Key = "zh:column", Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch, Padding = new Edges4(0f, 0f, 0f, U.PageBottom),
            Children =
            [
                Meta(overlay, model.TotalVisits, model.UniqueRoutes, lay) with { Margin = new Edges4(0f, ZuneSettingsRules.TitleBodyGap, 0f, 0f) },
                Tools(overlay, search, filterIndex, sortIndex, filters, sorts, lay) with { Margin = new Edges4(0f, U.HeadGap, 0f, U.HeadBottom) },
                body,
            ],
        };
        return ZuneFrameRules.PageRow(new BoxEl
        {
            Key = "zh:page", Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
            // The panorama stands over the scroll, as Settings' does: it bleeds left of the gutter, which a scroll's clip would cut.
            Children =
            [
                ZuneSettings.ScreenTitle(lay, Loc.Get(Strings.Nav.History.Title), TitleKey, wraps: false),
                ScrollView(column) with { Grow = 1f, Shrink = 1f, MinHeight = 0f, Key = "zh:scroll" },
            ],
        }, lay.Gutter);
    }

    // ── the head ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>"9 visits · 9 unique" in the meta voice, then "clear all" as a word at the row's end (it asks first: Clear also deletes the file).</summary>
    static BoxEl Meta(IOverlayService? overlay, int visits, int unique, ZuneSettingsLayout lay)
    {
        string words = Strings.Nav.History.VisitCount(visits) + " · " + FormatCache.Int(unique) + " " + Loc.Get(Strings.Nav.History.Stat.Unique);
        Element clear = K.Link("zh:clear", Loc.Get(Strings.Nav.History.ClearAll), () => Controls.Confirm(
            overlay,
            Loc.Get(Strings.Nav.History.ClearAllConfirm),
            Loc.Get(Strings.Nav.History.ClearAllConfirmBody),
            Loc.Get(Strings.Nav.History.ClearAll),
            static () => HistoryVerbs.ClearAll()), lay.Target);
        return new BoxEl
        {
            Key = "zh:meta", Direction = 0, AlignItems = FlexAlign.Center, Gap = U.HeadGap, MinWidth = 0f,
            Children =
            [
                ZuneText.Meta(words) with { Color = Tok.TextTertiary, Grow = 1f, Shrink = 1f },
                clear,
            ],
        };
    }

    /// <summary>The find field and the two word rails: one row beside each other where the column has room; at Compact the field takes the column's
    /// width and each rail folds into one word that says its choice ("all ▾", "most recent ▾") and opens the choices as radio rows, a 40 target.</summary>
    static BoxEl Tools(IOverlayService? overlay, Signal<string> search, Signal<int> filterIndex, Signal<int> sortIndex, Controls.Words.Word[] filters,
        Controls.Words.Word[] sorts, ZuneSettingsLayout lay)
    {
        bool stack = lay.ListIsStrip;
        Element find = AutoSuggestBox.Create(
            Array.Empty<string>(),
            placeholder: ZuneText.Word(Loc.Get(Strings.Nav.History.SearchPlaceholder)),
            width: stack ? 0f : U.FindWidth,
            grow: stack ? 1f : 0f,
            text: search,
            onQuerySubmitted: q => search.Value = q,
            onChange: q => search.Value = q,
            minHeight: lay.Touch ? lay.Target : 0f,
            cornerRadius: R.Shape.For(ShapeRole.Control));
        if (stack)
            return new BoxEl
            {
                Key = "zh:tools", Direction = 1, Gap = U.HeadGap, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
                Children =
                [
                    find,
                    new BoxEl
                    {
                        Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.SpaceBetween, MinWidth = 0f,
                        Children = [PickWord(overlay, "zh:filter", s_filterKeys, filterIndex, lay.Target), PickWord(overlay, "zh:sort", s_sortKeys, sortIndex, lay.Target)],
                    },
                ],
            };
        Element filterRail = Controls.Words.Rail(filters, filterIndex, static () => R.Ink.AccentDecor);
        Element sortRail = Controls.Words.Rail(sorts, sortIndex, static () => R.Ink.AccentDecor);
        return
            new BoxEl
            {
                Key = "zh:tools", Direction = 0, Gap = U.HeadGap * 2f, MinWidth = 0f, AlignItems = FlexAlign.Center, Wrap = true,
                Children = [filterRail, new BoxEl { Grow = 1f }, sortRail, find],
            };
    }

    static readonly string[] s_filterKeys =
    [
        Strings.Nav.History.Filter.All, Strings.Nav.History.Filter.Playlists, Strings.Nav.History.Filter.Shows,
        Strings.Nav.History.Filter.Library, Strings.Nav.History.Filter.Search, Strings.Nav.History.Filter.Pages,
    ];

    static readonly string[] s_sortKeys = [Strings.Nav.History.Sort.MostRecent, Strings.Nav.History.Sort.MostVisited];

    /// <summary>A folded rail: one word saying the current choice, a chevron in the accent, and the choices as radio rows on a click.</summary>
    static Element PickWord(IOverlayService? overlay, string key, string[] keys, Signal<int> index, float target)
    {
        var box = new BoxEl
        {
            Key = key, Direction = 0, Gap = Spacing.XS, Height = target, MinWidth = target, Shrink = 0f, AlignItems = FlexAlign.Center,
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, ClickRequestsContext = true,
            Children =
            [
                R.Type.DenseTitle("") with
                {
                    Text = Prop.Of(() => Loc.Get(keys[Math.Clamp(index.Value, 0, keys.Length - 1)])), Case = R.Case.For(LabelKind.Navigation), MaxLines = 1,
                    Color = Tok.TextPrimary,
                },
                Icon(Icons.ChevronDown, U.PickChevron) with { Color = Prop.Of(static () => R.Ink.AccentDecor) },
            ],
        }.Interactive(Interaction.Subtle);
        if (Controls.IsNullOverlay(overlay)) return box;
        return box.WithContextMenu(overlay, () =>
        {
            var rows = new MenuFlyoutItem[keys.Length];
            int current = index.Peek();
            for (int i = 0; i < rows.Length; i++)
            {
                int at = i;
                rows[i] = MenuFlyoutItem.RadioItem(ZuneText.Word(Loc.Get(keys[i]), LabelKind.Navigation), current == at, () => index.Value = at);
            }
            return new ContextMenuModel(rows);
        });
    }

    // ── the groups ───────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The day groups (most recent) or the one fold (most visited): a 22 light head with its count, then flat rows.</summary>
    static BoxEl Groups(IReadOnlyList<HistorySection> sections, ZuneSettingsLayout lay)
    {
        var groups = new Element[sections.Count];
        for (int s = 0; s < groups.Length; s++)
        {
            var section = sections[s];
            var kids = new Element[section.Rows.Count + 1];
            kids[0] = GroupHead(section.Label, section.Count);
            for (int k = 0; k < section.Rows.Count; k++) kids[k + 1] = Row(section.Rows[k], lay);
            groups[s] = new BoxEl { Key = "zh:group:" + s, Direction = 1, Gap = U.GroupHeadGap, MinWidth = 0f, AlignItems = FlexAlign.Stretch, Children = kids };
        }
        return new BoxEl { Key = "zh:groups", Direction = 1, Gap = U.GroupGap, MinWidth = 0f, AlignItems = FlexAlign.Stretch, Children = groups };
    }

    static BoxEl GroupHead(string label, string? count)
    {
        var kids = new List<Element>(2) { ZuneText.Group(label) with { Wrap = TextWrap.NoWrap, MaxLines = 1, Shrink = 0f } };
        if (count is { Length: > 0 }) kids.Add(ZuneText.Meta(count) with { Color = Tok.TextTertiary, Shrink = 1f });
        return new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = U.GroupCountGap, MinWidth = 0f, Children = kids.ToArray() };
    }

    /// <summary>One visit: the glyph on the gutter (the accent's for a playlist), the title over its kind line, the time and the visit count at the
    /// end (in the kind line at Compact), the remove glyph. A route this build has no page for stays in the log, dimmed and inert; its remove stays live.</summary>
    static Element Row(in HistoryRowFacts e, ZuneSettingsLayout lay)
    {
        bool stacked = lay.Touch;
        string line = ZuneText.Word(e.KindLabel);
        if (e.Arg is { Length: > 0 } arg) line += " · " + arg;
        string count = e.VisitCount > 1 ? Strings.Nav.History.VisitMultiplier(e.VisitCount) : "";
        if (stacked)
        {
            line += " · " + e.Timestamp;
            if (count.Length > 0) line += " · " + count;
        }

        var kids = new List<Element>(5)
        {
            new BoxEl
            {
                Width = U.GlyphLane, Height = U.GlyphLane, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Children = [Icon(e.Glyph, U.Glyph, e.IsPlaylist ? R.Ink.AccentDecor : Tok.TextSecondary)],
            },
            new BoxEl
            {
                Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, Justify = FlexJustify.Center,
                Children =
                [
                    R.Type.TrackTitle(e.Title) with { Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                    R.Type.DenseMeta(line) with { Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                ],
            },
        };
        if (!stacked)
        {
            if (count.Length > 0) kids.Add(R.Type.DenseMeta(count) with { Color = Tok.TextTertiary, Shrink = 0f });
            kids.Add(R.Type.DenseMeta(e.Timestamp) with { Color = Tok.TextTertiary, Shrink = 0f });
        }
        float box = lay.Touch ? lay.Target : U.RemoveBox;
        kids.Add(new BoxEl
        {
            Width = box, Height = box, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = e.Remove,
            Opacity = U.RemoveRestOpacity, HoverOpacity = 1f,
            Children = [Icon(Icons.Cancel, U.RemoveGlyph, Tok.TextTertiary)],
        }.Interactive(Interaction.Subtle));

        bool reachable = e.Reachable;
        return new BoxEl
        {
            Direction = 0, Height = U.RowH, AlignItems = FlexAlign.Center, Gap = U.RowGap, MinWidth = 0f,
            Opacity = reachable ? 1f : U.InertRowOpacity,
            Role = AutomationRole.Button, Cursor = reachable ? CursorId.Hand : CursorId.Arrow,
            OnClick = reachable ? e.Open : null,
            Children = kids.ToArray(),
        }.Interactive(Interaction.Subtle, isEnabled: reachable);
    }
}
