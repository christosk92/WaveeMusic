// ── Themes/Zune/Structure/Views/Screens/ZuneSettings.cs — Zune's `Pages.Settings` over the settings registry (settings rework phase 4) ─────────────────
//
// Boards: the settings concept's Z-Home, Z-Results, Z-Compact (handoff/settings-2026-11-18/boards), and the repo's Z-Settings-Appearance, Z-Settings-Playback,
// Z-Settings-Sections and Z-Theme-Options at every size for the row grammar.
//
// THE PICTURE. A panorama title ("settings", 120/130 at the light weight, 64/72 under the compact width, 150/160 at Huge; nothing on the page shrinks it), then a
// column of the "find a setting" pill over the lowercase SECTION LIST (ten words: "settings", the home, and the nine categories; the open word in the ink and
// heavier, the rest tertiary; a long word wraps inside its target) beside the open category's column (a 40/48 heading, then every group a 26/32 heading over its
// label | control rows); on Appearance the options' preview beside the column. The home's column is "your setup": the six facts at a glance as label | value rows
// (the value in the 22/28 light voice, a door to its dial), the suggestions as lowercase lines with an accent action word, the recently changed rows. Typing in
// the pill replaces the column with the board's two-line results (the label with the matched letters in the accent over the breadcrumb), links that open the
// dial in its category; nothing matching says so in the hint voice with the "did you mean" words in the accent.
//
// COMPACT (Z-Compact): the page is the drill-in list (60-tall rows: the category's name at 26/32/300, the old page's size, over its at-a-glance summary, a chevron) under the 40-tall
// pill and the suggestion line; a category opens as its own page under a "‹ settings" crumb (the ROUTE's open section: Back closes it, a link opens it). The
// results show under the pill in the list's place.
//
// WHAT STAYS CORE (contract 1.116, `SettingsPageProps`): the open category and its cell, the ten entries, every body (which dials exist, their words, values,
// enablement, reasons, children, explanation badges, "also in" places), the search (query, ranking, keys, focus), the home's facts and a link's arrival. This
// view draws them in Zune's grammar and writes through each dial's writer; it never touches the settings store (WVTH002).
//
// NODE IDENTITY. The pill, the list, its ten words, the lane and the column are the same nodes at every tier: a crossing changes their direction, widths, sizes
// and visibility (live props), never their tree, so nothing remounts. The lane's child is keyed per category and for the results, so a switch is the page's
// cross-fade and each category keeps its own scroll offset.
//
// THE ARRIVAL (design 4.9): the core opens the dial's category (Compact: its page); `ArrivalHost` brings the row (`zs:row:<dialId>`, else its nearest composed
// parent) to 0.3 of the viewport with the glide (a cut under reduced motion) and gives its control the keyboard; the row's label takes the accent and fades back
// to the ink over the policy's `HighlightMs` (`ZuneSettings.Explain.cs`, `LitLabel`).

using System.Collections.Immutable;
using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;
using Z = Wavee.Themes.Zune.ZuneSettingsRules;

namespace Wavee.Themes.Zune;

/// <summary>Zune's Settings page.</summary>
internal static partial class ZuneSettings
{
    /// <summary>Registers the Settings page and the Settings surfaces the setup wizard and the in-context panels share with it.</summary>
    internal static void Register(ViewRegistryBuilder views)
    {
        views.Add(ViewKeys.SettingsPage, Template, "zune.screens.settings");
        views.Add(ViewKeys.SettingsThemePicker, ThemePickerTemplate, "zune.screens.settings.theme-picker");
        views.Add(ViewKeys.SettingsThemeOptions, ThemeOptionsTemplate, "zune.screens.settings.theme-options");
    }

    /// <summary>The page title's node (the lint guide finds it).</summary>
    internal const string TitleKey = "zs:title";

    /// <summary>The section list's node (the pill and the words).</summary>
    internal const string SectionsKey = "zs:sections";

    static readonly BoxEl s_none = new() { Width = 0f, Height = 0f, Grow = 0f, Shrink = 0f, HitTestVisible = false };

    /// <summary>The registered template of <see cref="ViewKeys.SettingsPage"/>.</summary>
    public static Element Template(SettingsPageProps props, in ViewContext ctx)
    {
        bool rendered = ZuneMotion.Rendered(ctx.Hooks);
        TierFacts tiers = ctx.Tier();
        var lay = ZuneSettingsTiers.Of(tiers);
        var k = new ZuneKit(lay);
        var cats = props.Categories;
        int open = cats.Length == 0 ? 0 : Math.Clamp(props.Category, 0, cats.Length - 1);
        string slug = cats.Length == 0 ? "home" : cats[open].Slug;
        var search = props.Search;

        // Compact: the page is the drill-in list (the home), and a category opens as its own page under the crumb. The open page is the ROUTE's
        // (`OpenSection`: Back closes it, a link opens it, a crossing keeps it: from Small up the same category shows in the column layout).
        bool drill = lay.ListIsStrip;
        bool sectionPage = drill && props.OpenSection && open > 0;
        bool listPage = drill && !sectionPage;

        // The home's facts: the drill-in rows' summaries and the suggestion line at Compact, the home's column from Small up.
        SettingsHomeFacts? home = listPage || open == 0 ? props.Home.Read() : null;

        // On a Compact category page the panorama title is the category's name above the lane, and the title's gap to the body moves into the lane's
        // top air (the viewport starts right under the title).
        float laneAir = sectionPage ? Z.TitleBodyGap + Z.LaneTop : Z.LaneTop;
        Element content = listPage ? s_none
            : open == 0 ? HomeColumn(k, home!, cats)
            : CategoryColumn(k, props, open, heading: !drill);
        var scrolled = new List<Element>(2) { content };
        if (!listPage && lay.ShowsAside && string.Equals(slug, AppearanceSlug, StringComparison.Ordinal)) scrolled.Add(Aside(lay));

        // A column with the preview beside it wraps the preview under it where there is no room; a column alone is a plain row, so its basis (the tier's
        // column width) holds (a wrapping row breaks its lines on measured sizes, never on a basis).
        Element categoryLane = ScrollView(new BoxEl
        {
            Direction = 0, Wrap = scrolled.Count > 1, Gap = Z.AsideGap, MinWidth = 0f, AlignItems = FlexAlign.Start,
            // the clip reaches LaneInk left of the column, the column stays where it was (a left-hanging glyph at the gutter keeps its ink)
            Padding = new Edges4(Z.LaneInk, laneAir, 0f, Z.LaneBottom), Children = scrolled.ToArray(),
        }) with { Grow = 1f, Shrink = 1f, MinHeight = 0f, Margin = new Edges4(-Z.LaneInk, 0f, 0f, 0f), ScrollKey = "zs:" + slug, Key = "zs:scroll:" + slug };

        Element lane = new BoxEl
        {
            Key = "zs:lane", Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, MinHeight = 0f,
            // The Compact list page shows the lane only while the pill has a query (the results stand in the list's place).
            Visible = listPage ? Prop.Of(() => IsSearching(search)) : true,
            Children =
            [
                Embed.Comp(new LaneProps(categoryLane, slug, search, lay, laneAir), static () => new LaneSwitch()),
                Embed.Comp(new ArrivalProps(props.Focus, open), static () => new ArrivalHost()),
            ],
        };

        Element body = new BoxEl
        {
            Key = "zs:body", Direction = (byte)(drill ? 1 : 0), Gap = drill ? 0f : lay.BodyGap, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
            Padding = new Edges4(0f, drill ? 0f : Z.TitleBodyGap, 0f, 0f), AlignItems = FlexAlign.Stretch,
            Children = [Sections(k, props, open, listPage, sectionPage, home, rendered), lane],
        };

        // The page column (A1): capped at 2400 + the gutters and centred, so the title and the body share the band's x at every width.
        return ZuneFrameRules.PageRow(new BoxEl
        {
            Key = "zs:page", Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
            Children = sectionPage
                ? [CrumbRow(() => props.CloseSection()), PanoramaTitle(lay, cats[open].Title), body]
                : [PanoramaTitle(lay, null), body],
        }, lay.Gutter);
    }

    /// <summary>The Appearance category's slug: its column has the options' preview beside it.</summary>
    const string AppearanceSlug = "appearance";

    /// <summary>The pill has a ranked query (read inside a bind: the results' version subscribes it).</summary>
    static bool IsSearching(SettingsSearchModel search) => search.Version.Value >= 0 && search.Active;

    // ── the title ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The panorama title: the largest voice at the light weight, bled to the left of the gutter by its optical inset; its box reserves the larger
    /// size's line (the title scales from its left edge when the tier steps, so nothing under it moves).</summary>
    static Element PanoramaTitle(ZuneSettingsLayout lay, string? section = null)
        => ScreenTitle(lay, section ?? Loc.Get(Strings.Settings.Title), TitleKey, wraps: section is not null);

    /// <summary>A utility screen's panorama title (settings, history, customize home): the largest voice at the light weight, lowercase, bled to the
    /// left of the gutter by its optical inset. One line in the title zone's height (<paramref name="wraps"/> false), or wrapping whole words over
    /// two lines and hugging them: a long word never shrinks (owner rule), it takes the next line.</summary>
    internal static Element ScreenTitle(ZuneSettingsLayout lay, string text, string key, bool wraps)
    {
        TextEl title = lay.CompactTitle ? R.Type.PanoramaCompact(text) : R.Type.Panorama(text);
        if (lay.TitleSize > 0f) title = title with { Size = lay.TitleSize, LineHeight = lay.TitleLineH };   // Huge: the board's 150 over its 160 line
        // The line box is exactly the stated line height (the board's CSS line), set `TitleTop` over the body's start; the descenders hang below it.
        title = title with { LineStacking = LineStacking.BlockLineHeight };
        return new BoxEl
        {
            Key = key, Direction = 0, Shrink = 0f, MinWidth = 0f, Height = wraps ? float.NaN : lay.TitleH, Animate = ZuneMotion.TitleScale,
            // The page column pads the gutter; the panorama bleeds left of it by its optical inset (never past the column's edge).
            Margin = new Edges4(-MathF.Min(lay.Gutter, Z.PanoramaBleed), 0f, 0f, 0f),
            // A one-line title sits `TitleTop` into its fixed zone; a wrapping one hugs its lines from the top (a tier's step of the offset would move
            // its hugged height at once on a crossing).
            Children = [ZuneText.Cased(title, LabelKind.Heading) with { MaxLines = wraps ? Z.SectionTitleLines : 1, Wrap = wraps ? TextWrap.WrapWholeWords : TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Margin = new Edges4(0f, wraps ? 0f : lay.TitleTop, 0f, 0f) }],
        };
    }

    /// <summary>The open category's heading at the top of its column (Z-Settings-Playback: 40 / 48, light), wrapping whole words over two lines.</summary>
    static Element SectionHeading(string name) => new BoxEl
    {
        Key = "zs:heading", Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
        Children = [ZuneText.Title(name) with { MaxLines = Z.SectionTitleLines, Wrap = TextWrap.WrapWholeWords, Trim = TextTrim.CharacterEllipsis }],
    };

    /// <summary>The "‹ settings" crumb over a Compact category's page: one 40-tall target back to the list.</summary>
    static Element CrumbRow(Action close) => new BoxEl
    {
        Key = "zs:crumb", Direction = 0, AlignItems = FlexAlign.Center, Gap = Z.SectionRowGap, Shrink = 0f, MinWidth = 0f, MinHeight = Z.CrumbH, AlignSelf = FlexAlign.Start,
        Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = close,
        Children =
        [
            Icon(Icons.ChevronLeft, Z.SectionChevron, Tok.TextSecondary),
            ZuneText.Meta(Loc.Get(Strings.Settings.Title)) with { HoverColor = Tok.TextPrimary },
        ],
    }.Interactive(Interaction.Subtle);

    // ── the section list ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The pill over the section list: a column of the ten words beside the lane (Small and up), the drill-in page at Compact (nine rows with
    /// their summaries, the home being the page itself). ONE node, one child per entry at every tier; the direction, sizes, the summaries' and chevrons'
    /// visibility are the tier's. A word writes the category cell (it switches in place); a drill-in row opens the category as a page.</summary>
    static Element Sections(ZuneKit k, SettingsPageProps props, int open, bool listPage, bool sectionPage, SettingsHomeFacts? home, bool rendered)
    {
        var lay = k.Lay;
        bool drill = lay.ListIsStrip;
        var cats = props.Categories;
        var cell = props.CategoryCell();
        var search = props.Search;
        var items = new Element[cats.Length];
        for (int i = 0; i < items.Length; i++) items[i] = SectionItem(lay, props, cats[i], open, drill, cell, home);

        Element list = ScrollView(new BoxEl
        {
            Key = "zs:sections:list", Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
            // The drill-in page scrolls like a lane: nine 60-tall rows overrun a short window, and the last ones must still come up from under the play bar.
            Padding = new Edges4(0f, 0f, 0f, drill ? Z.LaneBottom : 0f), Children = items,
        }) with
        {
            Key = "zs:sections:scroll", Grow = 0f, Shrink = 1f, MinHeight = 0f, ContentSized = true, SuppressScrollBar = !drill, ScrollKey = "zs:sections",
            // The results stand in the drill-in list's place while the pill has a query.
            Visible = listPage ? Prop.Of(() => !IsSearching(search)) : true,
        };

        var kids = new List<Element>(3) { SearchPill(lay, search) };
        // The Compact page's suggestion line under the pill (Z-Compact); from Small up the home's column draws the suggestions. A declared swap family: a
        // crossing that drops it fades it, and it fades in on the way back.
        if (listPage && home is { Suggestions.Length: > 0 } facts)
            kids.Add(new BoxEl
            {
                Key = "zs:suggestions:compact", Direction = 1, MinWidth = 0f, Shrink = 0f, Padding = new Edges4(0f, 0f, 0f, Z.SuggestBelow),
                Enter = ZuneMotion.SwapEnter(rendered), Exit = ZuneMotion.SwapFade,
                Visible = Prop.Of(() => !IsSearching(search)),
                Children = [SuggestionsLines(k, props.Home, facts.Suggestions, "zs:suggest:compact:")],
            });
        kids.Add(list);
        return new BoxEl
        {
            Key = SectionsKey, Direction = 1, Width = drill ? float.NaN : lay.ListW, Shrink = drill ? 1f : 0f, MinWidth = 0f, MinHeight = 0f,
            AlignSelf = FlexAlign.Stretch, AlignItems = FlexAlign.Stretch, Visible = !sectionPage, Animate = ZuneMotion.ColumnCollapse,
            Children = kids.ToArray(),
        };
    }

    /// <summary>One entry: a word (the open one in the ink and heavier) or, at Compact, a drill-in row (the name at 26/32/300 over the at-a-glance summary, a
    /// chevron). The same node either way; the home's word has no row at Compact, where the page itself is the home.</summary>
    static Element SectionItem(ZuneSettingsLayout lay, SettingsPageProps props, SettingsCategoryEntry e, int open, bool drill, Signal<int> cell, SettingsHomeFacts? home)
    {
        int at = e.Index;
        bool isHome = at == 0;
        bool on = !drill && at == open;
        string slug = e.Slug;
        string name = isHome ? Loc.Get(Strings.Settings.Title) : e.Title;
        string summary = drill ? SummaryOf(e, home) : "";
        Action click = drill ? () => props.OpenCategory(slug) : () => cell.Value = at;
        return new BoxEl
        {
            Key = SettingsKeys.ZuneSection(slug), Direction = 0, AlignItems = FlexAlign.Center, Gap = drill ? Z.DrillGap : 0f, Shrink = 0f, MinWidth = 0f,
            MinHeight = lay.ListItemH, Padding = new Edges4(0f, drill ? Z.DrillPadY : 0f, 0f, drill ? Z.DrillPadY : 0f), Animate = ZuneMotion.PivotSlide,
            Visible = !(drill && isHome),
            Role = drill ? AutomationRole.Button : AutomationRole.Tab, Focusable = true, Cursor = CursorId.Hand, OnClick = click,
            Children =
            [
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f,
                    Children =
                    [
                        // A long word wraps inside its target rather than shrinking (design 7.3): the target grows a line.
                        Embed.Comp(new WordProps(name, on, drill, lay.NavSize, lay.NavLineH, props.Search), static () => new SectionWord()),
                        ZuneText.Hint(summary) with
                        {
                            Visible = drill && summary.Length > 0, MaxLines = Math.Max(1, lay.SummaryLines), Trim = TextTrim.WordEllipsis,
                        },
                    ],
                },
                new BoxEl { Shrink = 0f, Visible = drill, HitTestVisible = false, Children = [Icon(Icons.ChevronRight, Z.SectionChevron, Tok.TextTertiary)] },
            ],
        }.Interactive(Interaction.Subtle);
    }

    sealed record WordProps(string Name, bool Open, bool Drill, float Size, float LineHeight, SettingsSearchModel Search);

    /// <summary>A section word: the open one in the ink at the heavier weight, the rest tertiary. While the pill has a query no word is open (the results
    /// stand in the column, which no section owns); the open style comes back on the section that opens, or on the one that was open when the query clears.
    /// The same text node either way: only its brush (cross-faded) and its weight change, from the box's active state alone (a memo: a keystroke that keeps the
    /// results showing re-renders nothing here).</summary>
    sealed class SectionWord : Component
    {
        public override Element Render()
        {
            var p = UseProps<WordProps>();
            var search = p.Search;
            bool searching = UseComputed(() => IsSearching(search)).Value;
            bool on = p.Open && !searching;
            return ZuneText.Nav(p.Name) with
            {
                Color = p.Drill || on ? Tok.TextPrimary : Tok.TextTertiary, HoverColor = p.Drill ? R.Ink.AccentDecor : Tok.TextPrimary,
                BrushTransitionMs = Design.Motion.Faster,
                Weight = on ? Z.NavSelectedWeight : Z.NavRestWeight, Size = p.Size, LineHeight = p.LineHeight,
                MaxLines = 0, Wrap = TextWrap.WrapWholeWords, Trim = TextTrim.None, Shrink = 1f,
            };
        }
    }

    /// <summary>A drill-in row's summary (design 7.3): its category's fact at a glance where the home has one ("zune · system", "very high · normalization
    /// on"), else what the category is for. A glance tile carries its category's glyph name, which is unique per category.</summary>
    static string SummaryOf(SettingsCategoryEntry e, SettingsHomeFacts? home)
    {
        if (home is { } facts)
            foreach (var tile in facts.Glance)
                if (string.Equals(tile.Glyph, e.Glyph, StringComparison.Ordinal)) return tile.Value;
        return e.Subtitle;
    }

    // ── the "find a setting" pill ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The pill, bound to the page's search box (<c>Search.Box</c> keys it <see cref="SettingsKeys.Search"/>, routes its keys and takes the focus
    /// tickets): 32 tall r16 above the list, 40 tall r20 under the title at Compact, 36 tall r18 at Huge. One node at every tier.</summary>
    static Element SearchPill(ZuneSettingsLayout lay, SettingsSearchModel search)
        => search.Box(new BoxEl
        {
            Direction = 1, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, Shrink = 0f, Margin = new Edges4(0f, lay.SearchAbove, 0f, lay.SearchBelow),
            Children = [Embed.Comp(new PillProps(search, lay.SearchH, lay.SearchR, lay.Touch), static () => new SearchPillBody())],
        });

    sealed record PillProps(SettingsSearchModel Search, float Height, float Radius, bool Touch);

    /// <summary>The pill's inside: the magnifier, the field (a chromeless editor over the box's query cell; the box's keys run first), then "ctrl f" at rest
    /// (not on the touch page) or "clear" while it holds a query. The ring is the field rule's ink at rest and the accent while the field has the caret.</summary>
    sealed class SearchPillBody : Component
    {
        readonly Signal<bool> _focused = new(false);
        TemplateParts? _parts;

        public override Element Render()
        {
            var p = UseProps<PillProps>();
            var search = p.Search;
            bool focused = _focused.Value;
            _parts ??= new TemplateParts
            {
                [EditableText.PartRoot] = b => b with { Fill = ColorF.Transparent, HoverFill = ColorF.Transparent, PressedFill = ColorF.Transparent },
            };
            var parts = _parts;
            var focusedCell = _focused;
            Element field = new BoxEl
            {
                Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, Justify = FlexJustify.Center,
                Children =
                [
                    Embed.Comp(() => new EditableText
                    {
                        Text = search.Query, Placeholder = Prop.Of(static () => ZuneText.Word(Loc.Get(Strings.Theme.Zune.Screens.SettingsFind))),
                        Width = float.NaN, Height = Z.SearchFieldH, FontSize = Z.SearchFontSize, Chromeless = true, Parts = parts,
                        PreviewKeyDown = search.PreviewKey, OnFocusChanged = f => focusedCell.Value = f,
                    }),
                ],
            };
            Element hint = new BoxEl
            {
                Key = "zs:search:hint", Direction = 0, Shrink = 0f, Padding = Z.KeyHintPad, BorderWidth = Z.SearchRing, BorderColor = Tok.StrokeDividerDefault,
                // The key hint is for a keyboard: none on the touch page (Z-Compact), none while the field has the caret or a query.
                HitTestVisible = false, Visible = focused || p.Touch ? false : Prop.Of(() => search.Query.Value.Length == 0),
                Children = [R.Type.MicroMeta(Loc.Get(Strings.Theme.Zune.Frame.SearchHint)) with { Color = Tok.TextTertiary, MaxLines = 1, Wrap = TextWrap.NoWrap, Shrink = 0f }],
            };
            Element clear = new BoxEl
            {
                Key = "zs:search:clear", Direction = 0, Shrink = 0f, AlignItems = FlexAlign.Center, AlignSelf = FlexAlign.Stretch,
                Role = AutomationRole.Button, Cursor = CursorId.Hand, OnClick = search.Clear,
                Visible = Prop.Of(() => search.Query.Value.Length > 0),
                Children = [ZuneText.Command(Loc.Get(Strings.Detail.Filter.Clear)) with { Color = Tok.TextSecondary, HoverColor = Tok.TextPrimary }],
            }.Interactive(Interaction.Subtle);
            return new BoxEl
            {
                Key = "zs:search:pill", Direction = 0, AlignItems = FlexAlign.Center, Gap = Z.SearchGap, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
                Height = p.Height, Padding = new Edges4(Z.SearchPadL, 0f, Z.SearchPadR, 0f), Corners = CornerRadius4.All(p.Radius),
                BorderWidth = Z.SearchRing, BorderColor = focused ? R.Ink.AccentDecor : Tok.TextPrimary with { A = Z.SearchRingA },
                BrushTransitionMs = Design.Motion.Faster,
                Children = [Icon(Icons.Search, Z.SearchGlyph, focused ? Tok.TextSecondary : Tok.TextTertiary), field, hint, clear],
            };
        }
    }

    // ── the lane: the open category, or the results ───────────────────────────────────────────────────────────────────────

    sealed record LaneProps(Element Category, string Slug, SettingsSearchModel Search, ZuneSettingsLayout Lay, float LaneAir);

    /// <summary>The lane's one child: the open category's scroller (keyed per category) or the results' (while the pill has a query), cross-fading on a
    /// switch. It re-renders on the results' version alone: a keystroke rebuilds the results, never the page.</summary>
    sealed class LaneSwitch : Component
    {
        public override Element Render()
        {
            var p = UseProps<LaneProps>();
            var search = p.Search;
            _ = search.Version.Value;
            Element child = search.Active
                ? new BoxEl
                {
                    Key = "zs:lane:results", Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Animate = Z.LaneSwap,
                    Children = [ResultsLane(new ZuneKit(p.Lay), search, search.Highlight.Value, p.LaneAir)],
                }
                : new BoxEl
                {
                    Key = "zs:lane:" + p.Slug, Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Animate = Z.LaneSwap,
                    Children = [p.Category],
                };
            return new BoxEl { Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, MinHeight = 0f, Children = [child] };
        }
    }

    // ── the arrival ───────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The rows' nodes by dial id, as each realizes (a link's arrival finds its row here). A stale handle is caught by the scene's liveness check.</summary>
    static readonly Dictionary<string, NodeHandle> s_rowNodes = new(StringComparer.Ordinal);

    /// <summary>Remember a row's node (its <c>OnRealized</c>).</summary>
    static Action<NodeHandle> NoteRow(string dialId) => h => s_rowNodes[dialId] = h;

    /// <summary>The rows' control columns by dial id: the arrival gives the keyboard to the row's control, never to its "see it" or "also in" words.</summary>
    static readonly Dictionary<string, NodeHandle> s_controlNodes = new(StringComparer.Ordinal);

    /// <summary>Remember a row's control column (its <c>OnRealized</c>).</summary>
    static Action<NodeHandle> NoteControl(string dialId) => h => s_controlNodes[dialId] = h;

    /// <summary>The last arrival brought into view (a remounted lane never replays it).</summary>
    static int s_arrivedSeq;

    sealed record ArrivalProps(SettingsArrival Focus, int Category);

    /// <summary>Plays a link's arrival once the dial's category is laid out (a layout effect: the rows' bounds are valid): the row to 0.3 of its viewport with the
    /// glide (a cut under reduced motion), then the keyboard to its first control. A dial its category does not draw right now (a composite's inner row while
    /// its card is in another phase) lands on its nearest drawn parent. Draws nothing.</summary>
    sealed class ArrivalHost : Component
    {
        public override Element Render()
        {
            var p = UseProps<ArrivalProps>();
            var focus = p.Focus;
            var hooks = UseContext(InputHooks.Current);
            var post = UsePost();
            UseLayoutEffect(() => Arrive(focus, hooks, post), DepKey.From(focus.Seq, p.Category));
            return s_none;
        }

        void Arrive(SettingsArrival focus, InputHooks hooks, Action<Action> post)
        {
            if (focus.IsNone || focus.Seq == s_arrivedSeq || Context.Scene is not { } scene) return;
            string dial = focus.DialId;
            NodeHandle row = LiveRow(scene, dial);
            if (row.IsNull && focus.Parent is { } parent) row = LiveRow(scene, dial = parent);
            for (int i = focus.Expand.Length - 1; row.IsNull && i >= 0; i--) row = LiveRow(scene, dial = focus.Expand[i]);
            if (row.IsNull) return;
            NodeHandle controls = s_controlNodes.TryGetValue(dial, out var c) && !c.IsNull && scene.IsLive(c) ? c : row;
            s_arrivedSeq = focus.Seq;
            // A category that has just mounted has its rows laid out but its scroller has not measured its viewport yet (that lands with this frame's scroll
            // pass), and an aligned move is a share of the viewport: the glide starts on the next frame.
            post(() =>
            {
                if (!scene.IsLive(row)) return;
                scene.BringIntoView(row, Z.ArrivalAlign, Design.Reduced ? ScrollMove.Immediate : ScrollMove.Glide);
                NodeHandle control = hooks.FirstFocusableIn?.Invoke(controls) ?? NodeHandle.Null;
                if (!control.IsNull) hooks.FocusNode?.Invoke(control, false);
            });
        }

        static NodeHandle LiveRow(FluentGpu.Scene.SceneStore scene, string dialId)
            => s_rowNodes.TryGetValue(dialId, out var h) && !h.IsNull && scene.IsLive(h) ? h : NodeHandle.Null;
    }
}
