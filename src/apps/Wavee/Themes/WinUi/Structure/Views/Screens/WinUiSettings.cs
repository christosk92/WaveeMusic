// ── Themes/WinUi/Structure/Views/Screens/WinUiSettings.cs — WinUI's `Pages.Settings` over the settings registry (settings rework phase 4) ─────────────────
//
// Boards: the concept boards of handoff/settings-2026-11-18 (Main = the Settings home, W-Search, W-Arrival, W-Compact) over the repo's W-Settings grammar
// (-Compact, -Small, -Huge), W-SettingsSections, W-ThemeOptions. The page's PICTURE, Windows 11 Settings in Wavee's page:
//   · the HEAD: the title "Settings" (40/52; 28/36 at Compact and Small) and Windows Settings' own "Find a setting" AutoSuggestBox, right of the title at
//     Standard and Huge (360 wide), under it at Compact and Small (the column's width). One tree in every tier: the head flips its direction, so a crossing
//     keeps the title and the box (its text, caret, open list) as the same nodes;
//   · the STRIP: a SelectorBar of Home and the nine categories' short names (owner decision 2: the strip, not an inner list), keyed
//     `settings:cat:<slug>`, writing the page's category cell (no history step); it scrolls sideways with its edge fade where the ten run past the page;
//   · the BODY, one keyed scroller per view so each keeps its offset, swapped with the page cross-fade (`WinUiSettingsRules.TabSwap`): the Settings home
//     (`WinUiSettings.Home.cs`), a category's SettingsCards over the registry (`WinUiSettings.Rows.cs`, the cards `WinUiSettings.Body.*.cs`), or the
//     results page after Enter in the box (`WinUiSettings.Search.cs`). The cards' column is capped at 1000 (1200 at Huge, the whole width at Compact) and sits
//     on the page's left edge; at Huge the Appearance category's preview column rides beside it.
//
// WHAT STAYS CORE (the props): which category is open and its cell, the registry's bodies and rows (words, values, writers, enablement, children, badges,
// "Also in"), the home's facts, the search box model (its query, ranking, keys and focus tickets), a link's arrival, the Storage census. This view draws them
// and calls the verbs they carry; it never reaches the store (WVTH002) and never rebuilds a category itself.
//
// THE SHAPE. One template whose hooks sit above every branch. The numbers are `Rules\WinUiSettingsRules.cs` and the tier records `Rules\Tiers.Screens.Settings.cs`.

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

namespace Wavee.Themes.WinUi;

/// <summary>WinUI's Settings page.</summary>
internal static partial class WinUiSettings
{
    /// <summary>Registers the Settings page and the two Settings surfaces the setup wizard shares with it. (The explanation badge is registered through
    /// <c>CommonBlocks.Register</c>, which owns the key's one template.)</summary>
    internal static void Register(ViewRegistryBuilder views)
    {
        views.Add(ViewKeys.SettingsPage, Template, "winui.screens.settings");
        views.Add(ViewKeys.SettingsThemePicker, ThemePickerTemplate, "winui.screens.settings.theme-picker");
        views.Add(ViewKeys.SettingsThemeOptions, ThemeOptionsTemplate, "winui.screens.settings.theme-options");
    }

    /// <summary>The results page is on (Enter in the box, Windows Settings' second step), root-lifetime: a remounted root starts on the category.</summary>
    static readonly RootState<Signal<bool>> s_resultsState = new("WinUi.Settings.Results", static () => new Signal<bool>(false));

    /// <summary>The registered template of <see cref="ViewKeys.SettingsPage"/>.</summary>
    public static Element Template(SettingsPageProps props, in ViewContext ctx)
    {
        var hooks = ctx.Hooks;
        TierFacts tiers = ctx.Tier();
        // The page's tier record: read once, here, so the first frame is already in its final form; the static builders and the components that draw
        // controls read it through `s_layout`, which each of them sets from the same facts before it builds a control.
        var layout = s_layout = WinUiSettingsTiers.Of(tiers);
        var search = props.Search;
        var results = s_resultsState.Value;

        // UNCONDITIONAL — above every branch (hook order).
        hooks.UseEffect(() => (Action?)(static () => CloseVideoOverrideManager()), DepKey.Empty);
        // The route is keep-alive: a PARKED page is not unmounted, so the same teardown rides the deactivation edge.
        hooks.UseActivation(onDeactivated: static () => CloseVideoOverrideManager());
        // An emptied box (a clear, Escape, a link: the core clears it) leaves the results page. Auto-tracked: it re-runs on the query alone.
        hooks.UseEffect(() =>
        {
            if (search.Query.Value.Length == 0 && results.Peek()) results.Value = false;
        });

        int category = Math.Clamp(props.Category, 0, Math.Max(0, props.Categories.Length - 1));
        string slug = props.Categories.IsDefaultOrEmpty ? "home" : props.Categories[category].Slug;
        bool showResults = results.Value && search.Active;
        bool playback = !showResults && slug == "playback";
        hooks.UseEffect(() => { if (!playback) CloseVideoOverrideManager(); }, DepKey.From(playback ? 1 : 0));

        Element body;
        string view;
        if (showResults)
        {
            view = "search";
            body = Embed.Comp(new ResultsProps(props), static () => new ResultsPage()) with { Key = "settings:results" };
        }
        else if (category == 0)
        {
            view = "home";
            body = Embed.Comp(new HomeProps(props), static () => new HomePage()) with { Key = "settings:home" };
        }
        else
        {
            view = slug;
            body = CategoryBody(props.Body.Of(category), props);
        }

        bool aside = layout.Aside && !showResults && slug == "appearance";
        // The body swaps with the page cross-fade: the lane is keyed per view, so a switch is a real unmount the engine fades out under the arriving view,
        // and each view keeps its own offset. The head and the strip are SIBLINGS of the scroller: nothing compacts, sticks or gains a shadow.
        Element content = new BoxEl
        {
            Key = "settings:view:" + view, Direction = 1, Grow = 1f, MinHeight = 0f, Animate = WinUiSettingsRules.TabSwap,
            Children =
            [
                ScrollView(new BoxEl
                {
                    Direction = 1,
                    Padding = new Edges4(0f, 0f, 0f, layout.InsetX),
                    Children = [ContentColumn(body, layout, aside ? ThemeAside() : null)],
                }) with { Grow = 1f, ScrollKey = "settings:" + view, Key = "settings:scroll:" + view },
            ],
        };

        return new BoxEl
        {
            Key = "settings:page", Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1, Gap = layout.PageGap,
            Padding = new Edges4(layout.InsetX, layout.InsetTop, layout.InsetX, 0f),
            // The page's insets follow the window tier: the head, the strip and the body glide to their new boxes on a crossing (no part rebuilds).
            Children =
            [
                Head(layout, search),
                TabStrip(layout, props, showResults ? -1 : category, results),
                new BoxEl { Key = "settings:body", Direction = 1, Grow = 1f, MinHeight = 0f, MinWidth = 0f, Layout = WinUiScreenMotion.Reflow, Children = [content] },
            ],
        };
    }

    // ══ THE HEAD ══════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The page title's node (the lint's inset edge and the tier tests find it).</summary>
    internal const string TitleKey = WinUiFrameLayout.PageTitleKey;

    /// <summary>The title and the box: a row (the box right-aligned, 360 wide) at Standard and Huge, a column (the box the column's width) at Compact and Small.
    /// The same two children in both, so a crossing re-lays the SAME nodes (see the file header).</summary>
    static Element Head(WinUiSettingsLayout layout, SettingsSearchModel search)
    {
        bool under = layout.SearchUnder;
        var frame = new BoxEl
        {
            Direction = 1, Shrink = 0f, MinWidth = 0f,
            Width = under ? float.NaN : layout.SearchWidth, AlignSelf = under ? FlexAlign.Stretch : FlexAlign.Center, Layout = WinUiScreenMotion.Reflow,
            Children = [Embed.Comp(new SearchFieldProps(search), static () => new SearchField())],
        };
        return new BoxEl
        {
            Key = "settings:head", Direction = under ? (byte)1 : (byte)0, Gap = under ? layout.PageGap : Spacing.L, MinWidth = 0f, Shrink = 0f,
            MaxWidth = layout.CapsColumn ? layout.ColumnMaxW : float.NaN,   // the box's right edge is the cards' (the concept board)
            Layout = WinUiScreenMotion.Reflow,
            AlignItems = under ? FlexAlign.Stretch : FlexAlign.Center,
            Children = [Title(layout), search.Box(frame)],
        };
    }

    /// <summary>The page title: Title Large 40/52 at Standard and Huge, Title 28/36 at Compact and Small (W-Settings, -Small, -Compact). Keyed
    /// `winui.page.title` for the lint's inset edge; the line box is the stated line height at every tier so nothing under it moves.</summary>
    static Element Title(WinUiSettingsLayout layout)
        => new BoxEl
        {
            Key = TitleKey, Direction = 0, Grow = layout.SearchUnder ? 0f : 1f, Shrink = 1f, MinWidth = 0f, AlignItems = FlexAlign.End, Layout = WinUiScreenMotion.Reflow,
            Children =
            [
                R.Type.PageHero(Loc.Get(Strings.Settings.Title)) with
                {
                    Size = layout.TitleSize, LineHeight = layout.TitleLine, Grow = 1f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                    LineStacking = LineStacking.BlockLineHeight,
                },
            ],
        };

    // ══ THE STRIP ═════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The strip (the concept boards: Home and the nine short names under the head, a hairline under the row). A SelectorBar in a sideways scroller:
    /// where the ten labels run past the page (Compact, Small) it scrolls, its end fades where more wait, and the selected item glides into view with the next word.
    /// A click writes the category cell (a switch in place, no history step) and leaves a results page; no item is selected while the results page shows.
    /// ONE tree at every tier: the tier sets the scroller's height and fade, so a crossing keeps the SAME selector bar (its pill, its focus).</summary>
    static Element TabStrip(WinUiSettingsLayout layout, SettingsPageProps props, int selected, Signal<bool> results)
    {
        var entries = props.Categories;
        var labels = new string[entries.Length];
        var keys = new string?[entries.Length];
        for (int i = 0; i < labels.Length; i++)
        {
            labels[i] = entries[i].Strip;
            keys[i] = SettingsKeys.Category(entries[i].Slug);
        }
        var cell = props.CategoryCell;
        var search = props.Search;
        Action<int> open = i =>
        {
            results.Value = false;
            search.Clear();
            cell().Value = i;
        };
        return Embed.Comp(new StripProps(labels, keys, selected, open), static () => new CategoryStrip()) with { Key = "settings:tabs", Layout = WinUiScreenMotion.Reflow };
    }

    sealed record StripProps(string[] Labels, string?[] Keys, int Selected, Action<int> Open);

    sealed class CategoryStrip : SettingsComponent
    {
        readonly Dictionary<string, NodeHandle> _items = new(StringComparer.Ordinal);
        readonly TemplateParts _parts = new();
        readonly Action<int> _choose;
        Action<int>? _open;
        int _chosen = -1;
        bool _seeded;

        public CategoryStrip()
        {
            _parts[SelectorBar.PartItem] = b => b.Key is { } key ? b with { OnRealized = h => _items[key] = h } : b;
            // The strip's own pick (a click, the arrow keys): the page is on screen and stays, so the word glides into view.
            _choose = i =>
            {
                _chosen = i;
                _open?.Invoke(i);
            };
        }

        public override Element Render()
        {
            var layout = UseLayout();
            var p = UseProps<StripProps>();
            _open = p.Open;
            int selected = p.Selected;
            string? key = (uint)selected < (uint)p.Keys.Length ? p.Keys[selected] : null;
            UseLayoutEffect(() =>
            {
                // Motion nobody can see does not run. A category the page is HANDED (a link, a door, a search result, history) always commits a Settings
                // route, which re-enters the page under the shell's page swap: the page is fully transparent while the word would glide, so the word is
                // put in place at once and the page's first visible frame shows it where it rests. Only the strip's own pick, on a page that stays on
                // screen, glides (and never under reduced motion or on the strip's first layout).
                bool own = _chosen == selected;
                _chosen = -1;
                if (key is not null && _items.TryGetValue(key, out var item) && !item.IsNull && Context.Scene is { } scene)
                    scene.BringIntoView(item, float.NaN, _seeded && own && !Design.Reduced ? ScrollMove.Glide : ScrollMove.Immediate,
                        WinUiSettingsRules.StripGutter);
                _seeded = true;
            }, DepKey.From(selected));
            var style = layout.TabHeight > 0f ? new SelectorBarStyle { ItemHeight = layout.TabHeight } : null;
            Element bar = SelectorBar.Create(p.Labels, new Signal<int>(selected), onChange: _choose, parts: _parts, style: style, itemKeys: p.Keys);
            var strip = ScrollView(new BoxEl { Direction = 0, Children = [bar] }, horizontal: true) with
            {
                Grow = 0f, Shrink = 0f, MinWidth = 0f, Height = layout.TabHeight > 0f ? layout.TabHeight : float.NaN, SuppressScrollBar = true,
                AutoEdgeFade = layout.TabsScroll, ScrollKey = "settings:tabs",
            };
            return new BoxEl
            {
                Direction = 1, Shrink = 0f, MinWidth = 0f, MaxWidth = layout.CapsColumn ? layout.ColumnMaxW : float.NaN,
                Children =
                [
                    // A mouse has no sideways wheel: where the words run past the column (a narrow window, an open pane, Compact), hovering the strip shows
                    // the shelves' ‹ › pager over its ends at the side that hides words (owner report 2026-11-19), at the tier's hit target.
                    Controls.StripPager.Create(strip, "settings:tabs", layout.Touch ? layout.Target : float.NaN),
                    Divider(),
                ],
            };
        }
    }

    // ══ THE COLUMN ════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The cards' column, capped by the tier's record (1000, 1200 at Huge, the whole width at Compact) and sitting on the page's left edge, never
    /// centred; at Huge the Appearance category's preview column rides beside it. ONE tree in every tier: the row holds the cards first and the preview
    /// column after them only where it shows, so a crossing to Huge adds a sibling and never re-parents the cards.</summary>
    static Element ContentColumn(Element body, WinUiSettingsLayout layout, Element? aside)
    {
        float cap = layout.CapsColumn ? layout.ColumnMaxW : float.NaN;
        bool beside = aside is not null;
        var cards = new BoxEl
        {
            Key = "settings:cards", Direction = 1, MinWidth = 0f, MaxWidth = cap, Basis = beside ? cap : 0f, Grow = beside ? 0f : 1f, Shrink = 1f,
            Layout = WinUiScreenMotion.Reflow, Children = [body],
        };
        return new BoxEl
        {
            Key = "settings:columns", Direction = 0, Gap = WinUiSettingsRules.AsideGap, AlignSelf = FlexAlign.Stretch, AlignItems = FlexAlign.Start, MinWidth = 0f,
            Children = beside ? [cards, aside!.Value] : [cards],
        };
    }
}
