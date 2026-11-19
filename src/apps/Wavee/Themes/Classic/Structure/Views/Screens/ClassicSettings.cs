// ── Themes/Classic/Structure/Views/Screens/ClassicSettings.cs — Classic's `Pages.Settings` over the settings registry (settings rework 7.1) ─────────
//
// The page's PICTURE: the masthead (the 24 gear and the "Settings" title, the "Find a setting" box right-aligned beside it, under it at Compact), the strip of
// the Settings home and the nine categories (one SelectorBar that scrolls with its edge fade where it does not fit), and ONE keyed scroll lane for the open
// body: the Settings home (`ClassicSettings.Home.cs`), a category (`ClassicSettings.Body.cs`: a section header per group, a SettingsCard per dial, a
// SettingsExpander for a dial with children, two reading columns at Huge) or, while the box holds a query, the results page (`ClassicSettings.Search.cs`).
// This file is the frame and the shared row grammar every body builds with (the section header, the card, the expander item, the value tag, the toggle over a
// dial, the glyph names). The composite cards Classic draws (the AI lyrics card, the equalizer curve, the storage census, the update hero, the attached videos,
// the playback runtime card, the saved reports) are the other `ClassicSettings.*.cs` files, drawn where their dial lives.
//
// WHAT STAYS CORE (contract 1.116, `SettingsPageProps`): which category is open (`Category`, `CategoryCell`), what each category composes (`Body.Of`: its groups,
// rows, words, values, enablement and reasons, children, badges, "Also in" places), what a link names (`Focus`: the dial, its expanders, the highlight's
// length), what the box ranks (`Search`), what the home shows (`Home.Read()`), every writer, the Storage census. This view draws them and writes through each
// row's writer; it never reaches the settings store (WVTH002) and never rebuilds a category from the registry.
//
// THE SHAPE. ONE template whose hooks are above any branch: the mount effect re-seeds the controls' stable signals (a ComboBox / Slider / NumberBox binds ONE
// signal for its life), the deactivation edge and leaving Playback tear down what only the visible Playback body may own (the video-override flyout and its
// anchor), and a link's arrival opens the expanders on its way (`OpenArrival`). The bodies are static functions over the props, re-run when the template
// re-runs (the props' `Revision`, the box's results, any signal a body reads); a control that needs a hook is an embedded child component. The body swaps with
// the page cross-fade (Q38, `ClassicSettingsRules.TabSwap`); only the scroller is keyed, per category, so each keeps its own offset. The numbers are
// `Rules\ClassicSettingsRules.cs`, the tiers `Rules\Tiers.Settings.cs`.

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

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's Settings page.</summary>
internal static partial class ClassicSettings
{
    /// <summary>Registers the Settings page and the two Settings surfaces the setup wizard shares with it (Classic's explanation badge rides
    /// <c>CommonBlocks.Register</c>, which owns the key's stand-in).</summary>
    internal static void Register(ViewRegistryBuilder views)
    {
        views.Add(ViewKeys.SettingsPage, Template, "classic.screens.settings");
        views.Add(ViewKeys.SettingsThemePicker, ThemePickerTemplate, "classic.screens.settings.theme-picker");
        views.Add(ViewKeys.SettingsThemeOptions, ThemeOptionsTemplate, "classic.screens.settings.theme-options");
    }

    /// <summary>The registered template of <see cref="ViewKeys.SettingsPage"/>.</summary>
    public static Element Template(SettingsPageProps props, in ViewContext ctx)
    {
        var hooks = ctx.Hooks;
        TierFacts tiers = ctx.Tier();
        float gutter = ClassicTiers.PageOf(tiers).Gutter;   // the one page gutter (F3), unconditional like every hook here
        // The page's tier record: read once, here, so the first frame is already in its final form; the bodies (static functions) and the components that draw
        // controls read it through `s_layout`, which every one of them sets from the same facts before it builds a control.
        var layout = s_layout = ClassicSettingsTiers.Of(tiers);
        // A Short window (330-679 tall; the page is 210 at the minimum) cannot spare the 80 the title takes above a 100-high body: the title gives way there (the
        // title bar's tab already says "Settings"); the box stays.
        bool titleGivesWay = tiers.Height == HeightTier.Short;

        // UNCONDITIONAL — above every branch (a conditional hook breaks hook ORDER).
        // The page's expanders' open cells (page lifetime: a remounted page opens them as a fresh navigation does). The bodies read it through `s_expanders`.
        s_expanders = hooks.UseRef(new Dictionary<string, Signal<bool>>(StringComparer.Ordinal)).Value;
        hooks.UseEffect(() =>
        {
            SeedControls();
            // Leaving Settings (unmount) tears down what only the visible Playback body may own: the video-override flyout and its anchor.
            return (Action?)(static () => EnterPlayback(false));
        }, DepKey.Empty);
        // The route is keep-alive: a PARKED page is not unmounted, so the same teardown rides the deactivation edge.
        hooks.UseActivation(onDeactivated: static () => EnterPlayback(false));

        var search = props.Search;
        _ = search.Version.Value;                            // a re-ranked query re-renders the results page
        bool results = search.Active;
        var categories = props.Categories;
        int category = categories.Length == 0 ? 0 : Math.Clamp(props.Category, 0, categories.Length - 1);
        string slug = categories.Length == 0 ? "home" : categories[category].Slug;
        bool playback = !results && string.Equals(slug, PlaybackSlug, StringComparison.Ordinal);
        hooks.UseEffect(() => EnterPlayback(playback), DepKey.From(playback ? 1 : 0));

        // A link's arrival (design 4.9): the page has opened the dial's category. Its expanders open: on a lane that is mounted already, on the expander's own
        // FlowReveal motion (`OpenArrival`, an effect); on a lane the link brings in, already open as they mount (`OpenArrivingLane`: fresh open cells, so the
        // arriving category cross-fades in with the row in place instead of growing under the fade). The row's own frame then glides it into view,
        // highlights it and gives its control the keyboard (`DialFrame`).
        SettingsArrival focus = props.Focus;
        s_arrival = focus;
        string lane = results ? ResultsLane : slug;
        var lastLane = hooks.UseRef("");
        bool arriving = !string.Equals(lastLane.Value, lane, StringComparison.Ordinal);
        lastLane.Value = lane;
        var openedSeq = hooks.UseRef(0);
        s_laneArriving = arriving;
        if (arriving && focus.Seq > openedSeq.Value) OpenArrivingLane(focus);
        openedSeq.Value = Math.Max(openedSeq.Value, focus.Seq);
        hooks.UseEffect(() => OpenArrival(focus), DepKey.From(focus.Seq));

        Element body = results ? ResultsBody(props) : category == 0 ? HomeBody(props) : CategoryBody(props, category);
        // The body swaps with the page cross-fade (Q38): the lane is keyed per category, so a switch is a real unmount the engine fades out under the arriving
        // one, and each category keeps its own offset.
        Element content = new BoxEl
        {
            Key = "settings:tab:" + lane, Direction = 1, Grow = 1f, MinHeight = 0f, Animate = ClassicSettingsRules.TabSwap,
            Children =
            [
                ScrollView(new BoxEl
                {
                    Direction = 1,
                    Padding = new Edges4(gutter, Spacing.L, gutter, gutter),
                    Children = [ContentColumn(body, layout)],
                }) with { Grow = 1f, ScrollKey = "settings:" + lane, Key = "settings:scroll:" + lane },
            ],
        };

        // W28: the masthead and the strip are SIBLINGS of the scroller — nothing compacts, sticks or gains a shadow.
        return new BoxEl
        {
            Grow = 1f, Direction = 1,
            Children =
            [
                Masthead(gutter, layout, props, titleGivesWay),
                CategoryStrip(layout, gutter, props, category),
                content,
            ],
        };
    }

    /// <summary>The Playback category's slug: its body owns the attached-videos flyout.</summary>
    const string PlaybackSlug = "playback";

    /// <summary>The results page's lane (its scroller keeps its own offset apart from every category's).</summary>
    const string ResultsLane = "results";

    /// <summary>The page mount: every control that binds one signal for its life is re-read from its dial (the page is a keep-alive route, so page-lifetime
    /// statics are seeded here).</summary>
    static void SeedControls()
    {
        SeedSystem();
        SeedAppearance();
        SeedPlayback();
        SeedStorage();
    }

    // ══ 1. THE MASTHEAD AND THE STRIP ═════════════════════════════════════════════════════════════════════════════════

    /// <summary>The masthead: the page title (40/52 display) behind the 24 gear glyph where the tier's record draws one (C-Settings-Compact / -Small / -Huge;
    /// the tuned Standard title stands alone), and the search box: right-aligned in the title's row, the record's <see cref="ClassicSettingsRules.SearchWidth"/>,
    /// or the row's width under the title at Compact. ONE tree at every tier: the row's direction and the box's width are live props, the glyph fades, the title
    /// and the box slide into their new places and the masthead's height reveals (the strip and the body ride it), so a crossing keeps the box (its text, caret
    /// and popup) as the same node.</summary>
    static Element Masthead(float gutter, ClassicSettingsLayout layout, SettingsPageProps props, bool titleGivesWay)
    {
        bool under = layout.SearchUnder;
        Element title = new BoxEl
        {
            Key = "settings:title", Direction = 0, Grow = under ? 0f : 1f, Basis = under ? float.NaN : 0f, MinWidth = 0f,
            AlignItems = FlexAlign.Center, Animate = ClassicSettingsRules.HeadSlide, Visible = !titleGivesWay,
            Children = layout.TitleGlyph
                ? [
                    new BoxEl
                    {
                        Key = TitleGlyphKey, Direction = 0, Width = ClassicSettingsRules.TitleGlyph, Height = ClassicSettingsRules.TitleGlyph, Shrink = 0f,
                        Margin = new Edges4(0f, 0f, ClassicSettingsRules.TitleGlyphGap, 0f), HitTestVisible = false, Animate = ClassicSettingsRules.HeadFade,
                        Children = [Icon(Icons.Settings, ClassicSettingsRules.TitleGlyph, Tok.TextPrimary)],
                    },
                    TitleText(),
                ]
                : [TitleText()],
        };
        // The row ends at the column (the board's max-width 1000, the pair of them at Huge): the box's right edge is the cards' right edge.
        return new BoxEl
        {
            Direction = 1, Padding = new Edges4(gutter, Spacing.L, gutter, Spacing.M), Animate = ClassicTierMotion.Resize,
            Children =
            [
                new BoxEl
                {
                    Key = "settings:masthead", Direction = (byte)(under ? 1 : 0), AlignItems = under ? FlexAlign.Stretch : FlexAlign.Center,
                    Justify = FlexJustify.End, Gap = under && !titleGivesWay ? ClassicSettingsRules.SearchUnderGap : 0f, AlignSelf = FlexAlign.Stretch,
                    MaxWidth = layout.Columns * layout.ColumnMaxW + (layout.Columns - 1) * layout.ColumnGap,
                    Children = [title, SearchBox(props.Search, layout)],
                },
            ],
        };
    }

    /// <summary>The title (keyed, so the gear arriving in front of it at a crossing never remounts it).</summary>
    static Element TitleText()
        => R.Type.SurfaceDisplay(Loc.Get(Strings.Settings.Title)) with
        {
            Key = "settings:title-text", Grow = 1f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        };

    /// <summary>The title glyph's node.</summary>
    internal const string TitleGlyphKey = "settings:title-glyph";

    /// <summary>The strip (board: the tabs under the title): the Settings home and the nine categories by their short names (`settings:cat:&lt;slug&gt;`: a
    /// driver finds and clicks one by its slug). It binds the category cell, so a click switches in place with no history step; a click while the box holds a
    /// query empties it (the category shows, not the results). Where the lane runs off the edge (Compact, Small, and wherever ten names do not fit) it scrolls,
    /// bleeding to the card's edges, its end fading where more wait; otherwise it ends at the column. The first label sits on the gutter either way. ONE tree at
    /// every tier (the strip always rides its scroller; the tier sets the items' height and the rule's inset), so a crossing keeps the SAME selector bar.</summary>
    static Element CategoryStrip(ClassicSettingsLayout layout, float gutter, SettingsPageProps props, int category)
    {
        var categories = props.Categories;
        var labels = new string[categories.Length];
        var keys = new string?[categories.Length];
        for (int i = 0; i < labels.Length; i++)
        {
            labels[i] = categories[i].Strip;
            keys[i] = SettingsKeys.Category(categories[i].Slug);
        }
        var style = layout.TabHeight > 0f ? new SelectorBarStyle { ItemHeight = layout.TabHeight, LeadingInset = ClassicSettingsRules.TabLeadingInset } : null;
        var search = props.Search;
        Element bar = SelectorBar.Create(labels, props.CategoryCell(), onChange: _ => search.Clear(), parts: s_stripParts, style: style, itemKeys: keys);
        Element lane = new BoxEl { Direction = 0, Padding = new Edges4(gutter, 0f, gutter, 0f), Children = [bar] };
        return new BoxEl
        {
            Key = "settings:tabs", Direction = 1, Shrink = 0f,
            Children =
            [
                Embed.Comp(new StripProps(lane, keys.Length > 0 ? keys[Math.Clamp(category, 0, keys.Length - 1)] ?? "" : "", layout.Touch ? layout.Target : float.NaN),
                    static () => new StripScroller()),
                Divider() with { Margin = layout.TabsScroll ? default : new Edges4(gutter, 0f, gutter, 0f) },
            ],
        };
    }

    /// <summary>The strip items' nodes by key (their realization, chained through the item part), so the strip can bring the open category's name into view.</summary>
    static readonly Dictionary<string, NodeHandle> s_stripItems = new(StringComparer.Ordinal);

    static readonly TemplateParts s_stripParts = new()
    {
        [SelectorBar.PartItem] = static b => b.Key is { } key ? b with { OnRealized = h => s_stripItems[key] = h } : b,
    };

    /// <summary>The strip's lane, the open category's key, and its pagers' hit target (Compact's 40; NaN keeps the stock button).</summary>
    sealed record StripProps(Element Lane, string SelectedKey, float PagerTarget);

    /// <summary>The strip's scroller. Where the names run off the edge, the open category's name is brought into view whenever the category changes (a link,
    /// a door, the home's tile), gliding (a cut the first time and with reduced motion), so the selection is never hidden past the fade. A mouse has no
    /// sideways wheel, so hovering a strip that runs off the edge shows the shelves' ‹ › pager over its ends at the side that hides names (owner report
    /// 2026-11-19: "without trackpad this is not scrollable"), at the tier's hit target.</summary>
    sealed class StripScroller : Component
    {
        NodeHandle _viewport;
        bool _seeded;
        readonly Action<NodeHandle> _capture;

        public StripScroller() => _capture = h => _viewport = h;

        public override Element Render()
        {
            var p = UseProps<StripProps>();
            string selected = p.SelectedKey;
            UseLayoutEffect(() =>
            {
                if (_viewport.IsNull || !s_stripItems.TryGetValue(selected, out var item) || item.IsNull || Context.Scene is not { } scene || !scene.IsLive(item)) return;
                scene.BringIntoView(_viewport, item, float.NaN, _seeded && !Design.Reduced ? ScrollMove.Glide : ScrollMove.Immediate, Spacing.S);
                _seeded = true;
            }, DepKey.From(selected.GetHashCode()));
            var strip = ScrollView(p.Lane, horizontal: true) with
            {
                // The bar's own height (its items plus its 4-DIP padding above and below), never the tier's item height: a scroller held to the item height
                // clipped the bar's foot, and the selected item's pill with it.
                Grow = 0f, Shrink = 0f, MinWidth = 0f, SuppressScrollBar = true,
                // Every tier: the fade draws only where names wait past the edge.
                AutoEdgeFade = true, ScrollKey = "settings:tabs", OnRealized = _capture,
            };
            return Controls.StripPager.Create(strip, "settings:tabs", p.PagerTarget);
        }
    }

    // ══ 2. THE COLUMN AND THE TWO HUGE COLUMNS ════════════════════════════════════════════════════════════════════════

    /// <summary>N1: a LEFT-aligned column capped by the tier's record (1000, or the pair of them at Huge) — never centred, never full-bleed.</summary>
    static Element ContentColumn(Element body, ClassicSettingsLayout layout) => new BoxEl
    {
        Direction = 1, MaxWidth = layout.Columns * layout.ColumnMaxW + (layout.Columns - 1) * layout.ColumnGap, AlignSelf = FlexAlign.Stretch, Children = [body],
    };

    /// <summary>The page's tier record as the bodies see it (see <see cref="Template"/>).</summary>
    static ClassicSettingsLayout s_layout = ClassicSettingsTiers.For(WidthTier.Standard);

    /// <summary>N2: cards 4 DIP apart. The body's first child sits on the column's top edge: a section header that opens it drops its 32-DIP top margin. A body
    /// with two or more groups splits them at the boundary that balances the two halves (<see cref="ClassicSettingsTiers.SplitAt"/>) AT EVERY TIER, so the tree is
    /// the same whatever the tier: the two halves are two keyed columns (`settings:column:0`, `:1`) in one pair box (`settings:columns`). Where the tier has two
    /// columns (Huge) the pair is a row, 32 apart, each column capped at 1000 and starting on its top edge; elsewhere the pair is a column, 4 apart, and the second
    /// half's first header keeps the space above it, so the stack reads exactly as one column. A crossing into or out of Huge keeps every group, row and expander
    /// as the SAME node, and the columns glide into their new boxes (<see cref="ClassicTierMotion.Reflow"/>).</summary>
    static Element TabStack(params Element[] children)
    {
        var layout = s_layout;
        if (!SplitSections(children, out var left, out var right)) return Column(children, layout, -1, pair: false);
        bool pair = layout.Columns > 1;
        return new BoxEl
        {
            Key = ColumnsKey, Direction = (byte)(pair ? 0 : 1), Gap = pair ? layout.ColumnGap : ClassicSettingsRules.CardSpacing,
            AlignSelf = FlexAlign.Stretch, AlignItems = pair ? FlexAlign.Start : FlexAlign.Stretch, Animate = ClassicTierMotion.Reflow,
            Children = [Column(left, layout, 0, pair), Column(right, layout, 1, pair)],
        };
    }

    /// <summary>One column of cards: 4 apart. The first column starts on its top edge; the second starts on its top edge only where it stands BESIDE the first
    /// (<paramref name="pair"/>), and below the first it keeps its opening header's space above. A column beside another grows to share the row, capped.</summary>
    static Element Column(Element[] children, ClassicSettingsLayout layout, int index, bool pair)
    {
        if (children.Length > 0 && children[0].Is(out BoxEl first) && (index <= 0 || pair))
        {
            // The space above a section is a section header's own top padding (inside its height reveal) or another opener's top margin: the first has none.
            if (first.Margin.Top > 0f) first = first with { Margin = first.Margin with { Top = 0f } };
            if (first.Padding.Top > 0f && first.Key is { } key && key.StartsWith(SectionKeyPrefix, StringComparison.Ordinal))
                first = first with { Padding = first.Padding with { Top = 0f } };
            children[0] = first;
        }
        return new BoxEl
        {
            Key = index < 0 ? ColumnKey : ColumnKey + ":" + index, Direction = 1, Gap = ClassicSettingsRules.CardSpacing,
            AlignSelf = pair ? FlexAlign.Start : FlexAlign.Stretch,
            Grow = pair ? 1f : 0f, Basis = pair ? 0f : float.NaN, MinWidth = 0f, MaxWidth = pair ? layout.ColumnMaxW : float.NaN,
            Animate = index < 0 ? null : ClassicTierMotion.Reflow,
            Children = children,
        };
    }

    /// <summary>The body's groups (a run that starts at a section header) split in two by weight; false when it has one group.</summary>
    static bool SplitSections(Element[] children, out Element[] left, out Element[] right)
    {
        var starts = new List<int>(12);
        for (int i = 0; i < children.Length; i++)
            if (children[i].Is(out BoxEl sectionBox) && sectionBox.Key is { } key && key.StartsWith(SectionKeyPrefix, StringComparison.Ordinal)) starts.Add(i);
        if (starts.Count == 0 || starts[0] != 0) starts.Insert(0, 0);   // rows before the first header are the first group
        var weights = new int[starts.Count];
        for (int g = 0; g < weights.Length; g++) weights[g] = (g + 1 < starts.Count ? starts[g + 1] : children.Length) - starts[g];
        int at = ClassicSettingsTiers.SplitAt(weights);
        if (at < 0) { left = children; right = []; return false; }
        int cut = starts[at];
        left = children[..cut];
        right = children[cut..];
        return true;
    }

    /// <summary>The body's column node: `settings:column` for a body with one group, `settings:column:0` and `:1` (in <see cref="ColumnsKey"/>) for a body with
    /// more, at every tier.</summary>
    internal const string ColumnKey = "settings:column";

    /// <summary>The pair box of a body with two or more groups: a row at Huge, a column elsewhere.</summary>
    internal const string ColumnsKey = "settings:columns";

    /// <summary>The key every section header carries (the column split starts a group at one).</summary>
    const string SectionKeyPrefix = "settings:section:";

    /// <summary>The key of a section header's caption (inside the header: the column split reads only the headers' own keys).</summary>
    internal const string SectionCaptionKeyPrefix = "settings:section-caption:";

    // ══ 3. THE SHARED ROW GRAMMAR (0.2.9 `SettingsPage.cs:192-290`, `SettingsShared.cs`) ══════════════════════════

    /// <summary>A group's header: 16-DIP glyph + BodyStrong title, optionally a caption. Where the tier's record keeps the caption under the title (Compact, the
    /// tuned Standard) it is ≤ 2 lines in the SAME column as the title and the header hangs from the top; where it rides the title's line (Small, Huge: the boards'
    /// baseline row) it is one line that ends in an ellipsis, 8 after the title. The record's space above (none above the body's first, <see cref="Column"/>) and
    /// under is the header's own padding, so a crossing that changes the header's height or spacing reveals it (`Design.Reveal.Resize`: the cards below ride it)
    /// while the caption slides between its slots; the tree is the same in every tier.</summary>
    static Element SectionHeader(string title, string? icon = null, string? subtitle = null)
    {
        var layout = s_layout;
        bool hasSub = subtitle is { Length: > 0 };
        bool inline = hasSub && layout.SectionInline;
        Element text = hasSub
            ? new BoxEl
            {
                Direction = (byte)(inline ? 0 : 1), Gap = inline ? ClassicSettingsRules.SectionInlineGap : Spacing.XXS,
                AlignItems = inline ? FlexAlign.End : FlexAlign.Stretch, Grow = 1f, Basis = 0f, MinWidth = 0f,
                Children =
                [
                    BodyStrong(title) with { Shrink = 0f },
                    new BoxEl
                    {
                        Key = SectionCaptionKeyPrefix + title, Direction = 1, MinWidth = 0f,
                        Grow = inline ? 1f : 0f, Basis = inline ? 0f : float.NaN, Animate = ClassicSettingsRules.HeadSlide,
                        Children =
                        [
                            Caption(subtitle!) with
                            {
                                Color = Tok.TextSecondary, MinWidth = 0f, Wrap = inline ? TextWrap.NoWrap : TextWrap.Wrap, MaxLines = inline ? 1 : 2,
                                Trim = inline ? TextTrim.CharacterEllipsis : TextTrim.None,
                            },
                        ],
                    },
                ],
            }
            : BodyStrong(title);

        return new BoxEl
        {
            Key = SectionKeyPrefix + title,
            Direction = 0, Gap = Spacing.S,
            AlignItems = hasSub && !inline ? FlexAlign.Start : FlexAlign.Center,
            Padding = new Edges4(0f, layout.SectionTop, 0f, layout.SectionBottom),
            Animate = Design.Reveal.Resize,
            AlignSelf = FlexAlign.Stretch,
            Children = icon is null
                ? [text]
                : [Icon(icon, ClassicSettingsRules.SectionGlyph, Tok.TextSecondary) with { Margin = new Edges4(0f, ClassicSettingsRules.SectionGlyphDrop, 0f, 0f) }, text],
        };
    }

    /// <summary>One settings row (a <see cref="SettingsCard"/>). Inert under the pointer unless click-enabled (W29 ①).</summary>
    static Element Row(string label, string? sub, Element? control = null, string? icon = null,
                       SettingsCard.ContentAlignment align = SettingsCard.ContentAlignment.Right,
                       bool isClickEnabled = false, Action? onClick = null, bool isEnabled = true, string? actionIcon = null,
                       SettingsCard.Style? style = null)
        => SettingsCard.Create(new SettingsCard.Options
        {
            Header = label,
            Description = sub,
            HeaderIcon = icon,
            Content = control,
            Alignment = Aligned(align, control),
            Style = style ?? InlineStyle(control),
            IsClickEnabled = isClickEnabled,
            IsActionIconVisible = isClickEnabled,
            ActionIcon = actionIcon,
            OnClick = onClick,
            IsEnabled = isEnabled,
        });

    /// <summary>One expander item (thresholds 0: never wraps, never drops its glyph).</summary>
    static Element Item(string label, string? sub, Element? control = null,
                        SettingsCard.ContentAlignment align = SettingsCard.ContentAlignment.Right,
                        bool isEnabled = true, bool isClickEnabled = false, Action? onClick = null, string? icon = null)
        => SettingsExpander.Item(label, sub, control, Aligned(align, control), isEnabled, isClickEnabled, onClick, icon, InlineItemStyle(control));

    /// <summary>A toggle's row at a tier that stacks the other controls keeps the board's label | switch row: the card's own wrap thresholds are switched off
    /// for it, and its control column is the switch's own cell instead of the stock 120. Null elsewhere: the stock card.</summary>
    static SettingsCard.Style? InlineStyle(Element? control)
        => s_layout.StacksControls && IsToggle(control)
            ? SettingsCard.DefaultStyle with { WrapThreshold = 0f, WrapNoIconThreshold = 0f, ContentMinWidth = s_layout.ToggleMinWidth }
            : null;

    /// <summary>An expander whose header holds a toggle keeps the board's label | switch header where the tier stacks the other controls (the header card's own
    /// wrap thresholds off, its control column the switch's cell). Null elsewhere: the stock expander.</summary>
    static SettingsExpander.Style? ToggleHeaderStyle(Element? control)
    {
        if (!(s_layout.StacksControls && IsToggle(control))) return null;
        var s = SettingsExpander.DefaultStyle;
        return s with { HeaderCardStyle = s.HeaderCardStyle with { WrapThreshold = 0f, WrapNoIconThreshold = 0f, ContentMinWidth = s_layout.ToggleMinWidth } };
    }

    /// <summary>The expander item's twin of <see cref="InlineStyle"/>: an item never wraps, so only its control column changes, to the switch's own cell, where
    /// the tier stacks the other controls. Null elsewhere: the stock item.</summary>
    static SettingsExpander.Style? InlineItemStyle(Element? control)
    {
        if (!(s_layout.StacksControls && IsToggle(control))) return null;
        var s = SettingsExpander.DefaultStyle;
        float w = s_layout.ToggleMinWidth;
        return s with { ItemCardStyle = s.ItemCardStyle with { ContentMinWidth = w }, ClickableItemCardStyle = s.ClickableItemCardStyle with { ContentMinWidth = w } };
    }

    /// <summary>The alignment a row's control gets in this tier: where the tier stacks its controls (Compact) a control sits UNDER its label, full width, instead of
    /// squeezing beside it — except a toggle, which keeps the label | state | switch row of the board. Asked for explicitly, not left to the card's own measured
    /// threshold (that one reads the card's width a frame late, so the first frame would be the wide form).</summary>
    static SettingsCard.ContentAlignment Aligned(SettingsCard.ContentAlignment align, Element? control)
        => align == SettingsCard.ContentAlignment.Right && s_layout.StacksControls && control is not null && !IsToggle(control)
            ? SettingsCard.ContentAlignment.Vertical
            : align;

    /// <summary>N4: a collapsed picker group's ANSWER in its header — 14 DIP secondary, one line, char-ellipsis.</summary>
    static Element ValueTag(string value) => new TextEl(value)
    {
        Size = ClassicSettingsRules.ValueTagSize, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
    };

    /// <summary>Wide content (a card strip) for an expander's <c>ItemsHeader</c>: padding (16, 12, 16, 12), deliberately no fill/border — the expander body
    /// already paints the group chrome.</summary>
    static Element ExpanderPanel(Element content) => new BoxEl
    {
        Direction = 1, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
        Padding = new Edges4(Spacing.L, Spacing.M, Spacing.L, Spacing.M),
        Children = [content],
    };

    /// <summary>The registry's dial <paramref name="id"/> (an unknown id throws: a typo fails loudly).</summary>
    static SettingsDial Dial(string id) => SettingsSpec.Dial(id);

    /// <summary>A compact toggle over a dial: a FRESH signal seeded from its value, its writer (the one verb that persists and applies), then the page bump when
    /// the writer asks for it.</summary>
    static Element RowToggle(SettingsDial dial, bool isEnabled = true)
        => Toggle(new Signal<bool>(dial.Current is true), on =>
        {
            if (dial.Writer.Toggle!(on)) Settings.Bump();
        }, isEnabled);

    /// <summary>One settings row drawn from a dial: its title, its description (the dial's REASON while it is greyed with one, else its caption —
    /// <paramref name="sub"/> overrides it for a live sentence), its glyph (the dial's, or <paramref name="icon"/>) and its enablement. The control is the view's.</summary>
    static Element DialRow(SettingsDial dial, Element? control = null, string? sub = null, string? icon = null,
                           SettingsCard.ContentAlignment align = SettingsCard.ContentAlignment.Right,
                           bool isClickEnabled = false, Action? onClick = null, string? actionIcon = null)
    {
        var facts = SettingsFacts.Live;
        string? description = sub ?? (dial.DescriptionKey(facts) is { } key ? Loc.Get(key) : null);
        return Row(Loc.Get(dial.TitleKey), description, control, icon ?? (dial.Glyph is { } glyph ? Glyph(glyph) : null),
            align, isClickEnabled, onClick, dial.Enabled(facts).Enabled, actionIcon);
    }

    /// <summary>N11: every destructive action confirms through the ONE dialog shape, Cancel the default button.</summary>
    static void ConfirmThen(string title, string body, string primaryText, Action onConfirm)
        => Controls.Confirm(SettingsContext.Overlay, title, body, primaryText, onConfirm);

    // ══ 4. GLYPHS (0.2.9 `SettingsGlyphs.cs`) ═════════════════════════════════════════════════════════════════════

    static readonly HashSet<string> s_unmappedGlyphs = new(StringComparer.Ordinal);

    /// <summary>A registry glyph NAME → its <see cref="Icons"/> constant. An unmapped name fails loudly in Debug at the offending row, and in Release logs
    /// <c>settings.glyph.unmapped</c> once per name and paints the gear.</summary>
    static string Glyph(string name) => name switch
    {
        "Globe" => Icons.Globe, "Link" => Icons.Link, "Devices" => Icons.Devices, "Device" => Icons.Device,
        "Code" => Icons.Code, "LocaleLanguage" => Icons.LocaleLanguage, "OpenInNewWindow" => Icons.OpenInNewWindow,
        "Settings" => Icons.Settings, "Clock" => Icons.Clock, "Document" => Icons.Document, "Refresh" => Icons.Refresh,
        "Brush" => Icons.Brush, "Sun" => Icons.Sun, "Zoom" => Icons.Zoom, "Font" => Icons.Font, "Design" => Icons.Design, "Home" => Icons.Home,
        "List" => Icons.List, "RowSize" => Icons.RowSize, "Picture" => Icons.Picture, "ViewList" => Icons.ViewList,
        "DockLeft" => Icons.DockLeft, "SplitView" => Icons.SplitView, "Edit" => Icons.Edit, "Microphone" => Icons.Microphone,
        "RefineSparkle" => Icons.RefineSparkle, "Filter" => Icons.Filter, "MusicNote" => Icons.MusicNote,
        "Headphones" => Icons.Headphones, "RadioTower" => Icons.RadioTower, "Volume" => Icons.Volume, "Play" => Icons.Play,
        "Speakers" => Icons.Speakers, "Equalizer" => Icons.Equalizer, "Audio" => Icons.Audio, "Movie" => Icons.Movie,
        "TvMonitor" => Icons.TvMonitor, "Pin" => Icons.Pin, "ThisPc" => Icons.ThisPc, "Album" => Icons.Album,
        "Folder" => Icons.Folder, "FolderOpen" => Icons.FolderOpen, "Tag" => Icons.Tag, "Delete" => Icons.Delete,
        "Attention" => Icons.Attention, "Download" => Icons.Download, "ChromeClose" => Icons.ChromeClose,
        "ChromeMinimize" => Icons.ChromeMinimize, "RevealPassword" => Icons.RevealPassword, "Contact" => Icons.Contact,
        "Info" => Icons.Info, "StatusWarning" => Icons.StatusWarning, "Camera" => Icons.Camera, "Forward" => Icons.Forward,
        "Shield" => Icons.Shield, "Warning" => Icons.Warning, "Repair" => Icons.Repair, "Bell" => Icons.Bell,
        _ => UnmappedGlyph(name),
    };

    static string UnmappedGlyph(string name)
    {
        System.Diagnostics.Debug.Fail("Settings glyph name '" + name + "' has no Icons mapping.");
        if (s_unmappedGlyphs.Add(name))
            Log.Warn("settings", "settings.glyph.unmapped: Settings glyph name has no Icons mapping; using the default glyph (name=" + name + ")");
        return Icons.Settings;
    }

    /// <summary>A dial's glyph: its registry glyph, else the one Classic always drew beside it (<see cref="ClassicGlyphOf"/>), else none.</summary>
    static string? GlyphOf(SettingsDial dial) => dial.Glyph is { } name ? Glyph(name) : ClassicGlyphOf(dial.Id);
}
