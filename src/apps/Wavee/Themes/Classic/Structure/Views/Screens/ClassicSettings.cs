// ── Themes/Classic/Structure/Views/Screens/ClassicSettings.cs — Classic's `Pages.Settings` (themes plan 6.1, T11b) ───────────────────────────
//
// From `Screens\Settings.UI.cs` (`SettingsPageView`, §3 the row grammar, §4 the glyphs). The page's PICTURE: the masthead, the seven-tab pivot over a
// per-tab keyed scroll lane, and the shared row grammar every tab builds with (the section header, the card, the expander item, the value tag, the
// compact toggle over a document row). The tabs' bodies are `ClassicSettings.<Tab>.cs`; the theme picker, the options and the sidebar layout cards are
// `ClassicSettings.Theme.cs` (they are their own keys: the setup wizard mounts them too).
//
// WHAT STAYS CORE. The document (`SettingsDoc`: which rows exist, their titles, values, enablement and reasons, visibility, glyph names, confirmations),
// every verb (`row.Writer`: persistence, live apply, DSP pushes, OS registrations, file I/O off the render path), the facts the rows ask (`SettingsFacts`), the
// page epoch (`Settings.Bump`), the Storage census (the props carry it), the pivot's root-lifetime cell and the deep link, and the page context the verbs read
// (`SettingsContext`). This view reads the document and writes through each row's writer; it never touches the settings store (WVTH002: it reads a row's value
// through `row.Current`).
//
// THE SHAPE. ONE template whose hooks are above the tab switch: the mount effect re-seeds the controls' stable signals (a ComboBox / Slider / NumberBox binds ONE
// signal for its life), the deactivation edge and the tab change tear down what only the visible Playback tab may own (the video-override flyout and its anchor).
// The tab bodies are static functions over the document, re-run when the template re-runs (the props' `Revision`, or any signal a body reads); a control that
// needs a hook is an embedded child component. The body swaps with the page cross-fade (Q38, `ClassicSettingsRules.TabSwap`); only the scroller is keyed, per
// tab, so each tab keeps its own offset. The numbers are `Rules\ClassicSettingsRules.cs`.

using FluentGpu;
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
using Catalog = Wavee.Settings.Catalog;
using Tab = Wavee.Settings.Tab;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's Settings page.</summary>
internal static partial class ClassicSettings
{
    /// <summary>Registers the Settings page and the two Settings surfaces the setup wizard shares with it.</summary>
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
        // The page's tier record: read once, here, so the first frame is already in its final form; the tab bodies (static functions) and the components
        // that draw controls read it through `s_layout`, which every one of them sets from the same facts before it builds a control.
        var layout = s_layout = ClassicSettingsTiers.Of(tiers);
        // A Short window (330-679 tall; the page is 210 at the minimum) cannot spare the 80 the title takes above a 100-high body: the title row gives way there
        // (the title bar's tab already says "Settings"), and the tab strip leads.
        bool titleGivesWay = tiers.Height == HeightTier.Short;

        // UNCONDITIONAL — above the tab switch (a conditional hook breaks hook ORDER).
        hooks.UseEffect(() =>
        {
            SeedControls();
            // Leaving Settings (unmount) tears down what only the visible Playback tab may own: the video-override flyout and its
            // anchor (0.2.9 `UnmountVideoOverrides`).
            return (Action?)(static () => EnterPlayback(false));
        }, DepKey.Empty);
        // The route is keep-alive: a PARKED page is not unmounted, so the same teardown rides the deactivation edge.
        hooks.UseActivation(onDeactivated: static () => EnterPlayback(false));

        int tab = Math.Clamp(props.Tab, 0, Settings.TabSlugs.Length - 1);
        hooks.UseEffect(() => EnterPlayback(tab == (int)Tab.Playback), DepKey.From(tab));

        Element body = (Tab)tab switch
        {
            Tab.Appearance => AppearanceTab(),
            Tab.Playback => PlaybackTab(),
            Tab.Notifications => NotificationsTab(),
            Tab.Storage => StorageTab(props.Storage),
            Tab.PrivacyDiagnostics => PrivacyDiagnosticsTab(),
            Tab.About => AboutTab(),
            _ => GeneralTab(),
        };

        string slug = Settings.TabSlugs[tab];
        // The body swaps with the page cross-fade (Q38): the lane is keyed per tab, so a switch is a real unmount the
        // engine fades out under the arriving tab, and each tab keeps its own offset. Every tab is an ordinary scrolling
        // lane (N8's unscrolled Logs lane is retired: the viewer is the route `logs`).
        Element content = new BoxEl
        {
            Key = "settings:tab:" + slug, Direction = 1, Grow = 1f, MinHeight = 0f, Animate = ClassicSettingsRules.TabSwap,
            Children =
            [
                ScrollView(new BoxEl
                {
                    Direction = 1,
                    Padding = new Edges4(gutter, Spacing.L, gutter, gutter),
                    Children = [ContentColumn(body, layout)],
                }) with { Grow = 1f, ScrollKey = "settings:" + slug, Key = "settings:scroll:" + slug },
            ],
        };

        // W28: the masthead and the strip are SIBLINGS of the scroller — nothing compacts, sticks or gains a shadow.
        return new BoxEl
        {
            Grow = 1f, Direction = 1,
            Children =
            [
                titleGivesWay ? new BoxEl { Key = "settings:title", Height = 0f, HitTestVisible = false } : Header(gutter, layout),
                TabStrip(layout, gutter, props),
                content,
            ],
        };
    }

    /// <summary>The page mount: every control that binds one signal for its life is re-read from the document (the page is a keep-alive route, so
    /// page-lifetime statics are the 0.2.9 instance fields, verbatim).</summary>
    static void SeedControls()
    {
        SeedGeneral();
        SeedAppearance();
        SeedPlayback();
        SeedStorage();
    }

    static string[] TabLabels() =>
    [
        Loc.Get(Strings.Settings.Tabs.General),
        Loc.Get(Strings.Settings.Tabs.Appearance),
        Loc.Get(Strings.Settings.Tabs.Playback),
        Loc.Get(Strings.Settings.Notify.Title),
        Loc.Get(Strings.Settings.Tabs.Storage),
        Loc.Get(Strings.Settings.Tabs.Privacy),
        Loc.Get(Strings.Settings.Tabs.About),
    ];

    // ══ 1. THE SHARED ROW GRAMMAR (0.2.9 `SettingsPage.cs:192-290`, `SettingsShared.cs`) ══════════════════════════

    /// <summary>The page head: the page title (40/52 display, no meta line), padded (gutter, 16, gutter, 12), behind the 24 gear glyph where the tier's record
    /// draws one (C-Settings-Compact / -Small / -Huge; the tuned Standard title stands alone). On a crossing the glyph fades in or out and the title slides
    /// beside it (both keyed, no tier in the keys).</summary>
    static Element Header(float gutter, ClassicSettingsLayout layout)
    {
        Element title = new BoxEl
        {
            Key = "settings:title", Direction = 0, Grow = 1f, Basis = 0f, MinWidth = 0f, Animate = ClassicSettingsRules.HeadSlide,
            Children = [R.Type.SurfaceDisplay(Loc.Get(Strings.Settings.Title)) with { Grow = 1f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f }],
        };
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.End,
            Padding = new Edges4(gutter, Spacing.L, gutter, Spacing.M),
            Children = layout.TitleGlyph
                ? [
                    new BoxEl
                    {
                        Key = TitleGlyphKey, Direction = 0, Width = ClassicSettingsRules.TitleGlyph, Height = ClassicSettingsRules.TitleGlyph, Shrink = 0f,
                        AlignSelf = FlexAlign.Center, Margin = new Edges4(0f, 0f, ClassicSettingsRules.TitleGlyphGap, 0f), HitTestVisible = false,
                        Animate = ClassicSettingsRules.HeadFade,
                        Children = [Icon(Icons.Settings, ClassicSettingsRules.TitleGlyph, Tok.TextPrimary)],
                    },
                    title,
                ]
                : [title],
        };
    }

    /// <summary>The title glyph's node (the tier tests find it where the record draws it).</summary>
    internal const string TitleGlyphKey = "settings:title-glyph";

    /// <summary>N1: a LEFT-aligned column capped by the tier's record (1000, or the pair of them at Huge) — never centred, never full-bleed.</summary>
    static Element ContentColumn(Element body, ClassicSettingsLayout layout) => new BoxEl
    {
        Direction = 1, MaxWidth = layout.Columns * layout.ColumnMaxW + (layout.Columns - 1) * layout.ColumnGap, AlignSelf = FlexAlign.Stretch, Children = [body],
    };

    /// <summary>The page's tier record as the tab bodies see it (see <see cref="Template"/>).</summary>
    static ClassicSettingsLayout s_layout = ClassicSettingsTiers.For(WidthTier.Standard);

    /// <summary>The tab strip (board: the tabs under the title). Where the lane runs off the edge (Compact, Small) it is ONE strip that scrolls, bleeding to the
    /// card's edges, its end fading where more wait; otherwise it ends at the column. The first label sits on the gutter either way.</summary>
    static Element TabStrip(ClassicSettingsLayout layout, float gutter, SettingsPageProps props)
    {
        var style = layout.TabHeight > 0f ? new SelectorBarStyle { ItemHeight = layout.TabHeight, LeadingInset = ClassicSettingsRules.TabLeadingInset } : null;
        Element bar = SelectorBar.Create(TabLabels(), props.TabCell(), style: style);
        if (!layout.TabsScroll)
            return new BoxEl { Direction = 1, Padding = new Edges4(gutter, 0f, gutter, 0f), Children = [bar, Divider()] };
        return new BoxEl
        {
            Key = "settings:tabs", Direction = 1, Shrink = 0f,
            Children =
            [
                ScrollView(new BoxEl { Direction = 0, Padding = new Edges4(gutter, 0f, gutter, 0f), Children = [bar] }, horizontal: true) with
                {
                    Grow = 0f, Shrink = 0f, MinWidth = 0f, Height = layout.TabHeight, SuppressScrollBar = true, AutoEdgeFade = true, ScrollKey = "settings:tabs",
                },
                Divider(),
            ],
        };
    }

    /// <summary>N2: cards 4 DIP apart. The tab's first child sits on the column's top edge: a section header that opens the tab drops its
    /// 32-DIP top margin (the gap above a section separates it from the one before; the first has none). Where the tier has two columns (Huge) the
    /// sections split over them at the boundary that balances the two (<see cref="ClassicSettingsTiers.SplitAt"/>); each column starts on its top edge.</summary>
    static Element TabStack(params Element[] children)
    {
        var layout = s_layout;
        if (layout.Columns > 1 && SplitSections(children, out var left, out var right))
            return new BoxEl
            {
                Direction = 0, Gap = layout.ColumnGap, AlignSelf = FlexAlign.Stretch, AlignItems = FlexAlign.Start,
                Children = [Column(left, layout, 0), Column(right, layout, 1)],
            };
        return Column(children, layout, -1);
    }

    /// <summary>One column of cards: 4 apart, its first child on its top edge. A column of a pair grows to share the row, each capped.</summary>
    static Element Column(Element[] children, ClassicSettingsLayout layout, int index)
    {
        bool pair = index >= 0;
        if (children.Length > 0 && children[0] is BoxEl first)
        {
            // The space above a section is a section header's own top padding (inside its height reveal) or another opener's top margin: the first has none.
            if (first.Margin.Top > 0f) first = first with { Margin = first.Margin with { Top = 0f } };
            if (first.Padding.Top > 0f && first.Key is { } key && key.StartsWith(SectionKeyPrefix, StringComparison.Ordinal))
                first = first with { Padding = first.Padding with { Top = 0f } };
            children[0] = first;
        }
        return new BoxEl
        {
            Key = pair ? ColumnKey + ":" + index : ColumnKey, Direction = 1, Gap = ClassicSettingsRules.CardSpacing, AlignSelf = pair ? FlexAlign.Start : FlexAlign.Stretch,
            Grow = pair ? 1f : 0f, Basis = pair ? 0f : float.NaN, MinWidth = 0f, MaxWidth = pair ? layout.ColumnMaxW : float.NaN, Children = children,
        };
    }

    /// <summary>The tab's sections (a run that starts at a section header) split in two by weight; false when it has one section.</summary>
    static bool SplitSections(Element[] children, out Element[] left, out Element[] right)
    {
        var starts = new List<int>(8);
        for (int i = 0; i < children.Length; i++)
            if (children[i] is BoxEl { Key: { } key } && key.StartsWith(SectionKeyPrefix, StringComparison.Ordinal)) starts.Add(i);
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

    /// <summary>The tab body's column node (`settings:column`, or `settings:column:0` and `:1` where the tier has two): the tier tests find them.</summary>
    internal const string ColumnKey = "settings:column";

    /// <summary>The key every section header carries (the column split starts a group at one).</summary>
    const string SectionKeyPrefix = "settings:section:";

    /// <summary>The key of a section header's caption (inside the header: the column split reads only the headers' own keys).</summary>
    internal const string SectionCaptionKeyPrefix = "settings:section-caption:";

    /// <summary>A group eyebrow: 16-DIP glyph + BodyStrong title, optionally a caption. Where the tier's record keeps the caption under the title (Compact, the tuned
    /// Standard) it is ≤ 2 lines in the SAME column as the title and the header hangs from the top; where it rides the title's line (Small, Huge: the boards'
    /// baseline row) it is one line that ends in an ellipsis, 8 after the title, its last line on the title's. The record's space above (none above the tab's first,
    /// <see cref="Column"/>) and under is the header's own padding, so a crossing that changes the header's height or spacing reveals it (`Design.Reveal.Resize`: the
    /// cards below ride it) while the caption slides between its slots; the tree is the same in every tier.</summary>
    static Element SectionHeader(string title, string? icon = null, string? subtitle = null)
    {
        var layout = s_layout;
        bool hasSub = subtitle is { Length: > 0 };
        bool inline = hasSub && layout.SectionInline;
        Element text = hasSub
            ? new BoxEl
            {
                Direction = inline ? 0 : 1, Gap = inline ? ClassicSettingsRules.SectionInlineGap : Spacing.XXS,
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
                       bool isClickEnabled = false, Action? onClick = null, bool isEnabled = true, string? actionIcon = null)
        => SettingsCard.Create(new SettingsCard.Options
        {
            Header = label,
            Description = sub,
            HeaderIcon = icon,
            Content = control,
            Alignment = Aligned(align, control),
            Style = InlineStyle(control),
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
        => SettingsExpander.Item(label, sub, control, Aligned(align, control), isEnabled, isClickEnabled, onClick, icon);

    /// <summary>A toggle's row at a tier that stacks the other controls keeps the board's label | switch row: the card's own wrap thresholds (it moves a control under
    /// its label when it measures narrower than 476) are switched off for it. Null elsewhere: the stock card.</summary>
    static SettingsCard.Style? InlineStyle(Element? control)
        => s_layout.StacksControls && IsToggle(control) ? SettingsCard.DefaultStyle with { WrapThreshold = 0f, WrapNoIconThreshold = 0f } : null;

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

    /// <summary>Wide content (a card strip) for an expander's <c>ItemsHeader</c>: padding (16, 12, 16, 12), deliberately
    /// no fill/border — the expander body already paints the group chrome.</summary>
    static Element ExpanderPanel(Element content) => new BoxEl
    {
        Direction = 1, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
        Padding = new Edges4(Spacing.L, Spacing.M, Spacing.L, Spacing.M),
        Children = [content],
    };

    /// <summary>The document's row <paramref name="id"/> of <paramref name="tab"/>.</summary>
    static SettingsRow D(Tab tab, string id) => SettingsDoc.Row(tab, id);

    /// <summary>A compact toggle over a document row: a FRESH signal seeded from the row's value, the row's writer (the one
    /// verb that persists and applies), then the page bump when the writer asks for it.</summary>
    static Element RowToggle(SettingsRow row, bool isEnabled = true)
        => Toggle(new Signal<bool>(row.Current is true), on =>
        {
            if (row.Writer.Toggle!(on)) Settings.Bump();
        }, isEnabled);

    /// <summary>One settings row drawn from a document row: its title, its description (the row's REASON while it is greyed
    /// with one, else its caption — <paramref name="sub"/> overrides it for a live sentence), its glyph (the catalog's, or
    /// <paramref name="icon"/>) and its enablement. The control is the view's.</summary>
    static Element DocRow(SettingsRow row, Element? control = null, string? sub = null, string? icon = null,
                          SettingsCard.ContentAlignment align = SettingsCard.ContentAlignment.Right,
                          bool isClickEnabled = false, Action? onClick = null, string? actionIcon = null)
    {
        var facts = SettingsFacts.Live;
        string? description = sub ?? (row.DescriptionKey(facts) is { } key ? Loc.Get(key) : null);
        return Row(Loc.Get(row.TitleKey), description, control, icon ?? (row.GlyphRole is { } glyph ? Glyph(glyph) : null),
            align, isClickEnabled, onClick, row.Enabled(facts).Enabled, actionIcon);
    }

    /// <summary>N11: every destructive action confirms through the ONE dialog shape, Cancel the default button.</summary>
    static void ConfirmThen(string title, string body, string primaryText, Action onConfirm)
        => Controls.Confirm(SettingsContext.Overlay, title, body, primaryText, onConfirm);

    // ══ 2. GLYPHS (0.2.9 `SettingsGlyphs.cs`) ═════════════════════════════════════════════════════════════════════

    static readonly HashSet<string> s_unmappedGlyphs = new(StringComparer.Ordinal);

    /// <summary>A catalog glyph NAME → its <see cref="Icons"/> constant. An unmapped name fails loudly in Debug at the
    /// offending row, and in Release logs <c>settings.glyph.unmapped</c> once per name and paints the gear.</summary>
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
        "Shield" => Icons.Shield, "Warning" => Icons.Warning, "Repair" => Icons.Repair,
        _ => UnmappedGlyph(name),
    };

    static string UnmappedGlyph(string name)
    {
        System.Diagnostics.Debug.Fail("Settings glyph name '" + name + "' has no Icons mapping.");
        if (s_unmappedGlyphs.Add(name))
            Log.Warn("settings", "settings.glyph.unmapped: Settings glyph name has no Icons mapping; using the default glyph (name=" + name + ")");
        return Icons.Settings;
    }

    static string SectionGlyph(Tab tab, string title) => Glyph(Catalog.SectionGlyph(tab, title));

    static string RowGlyph(Tab tab, string rowId) => Glyph(Catalog.RowGlyph(tab, rowId));
}
