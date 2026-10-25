// ── Themes/WinUi/Structure/Views/Screens/WinUiSettings.cs — WinUI's `Pages.Settings` (themes plan 6.2, P6 W8b) ───────────────────────────
//
// Boards: W-Settings (-Compact, -Small, -Huge), W-SettingsSections, W-ThemeOptions. The page's PICTURE: the title (28 at Compact and Small, 40 above), a SelectorBar of
// the SEVEN tabs the structure's grouping declares (`WinUiPolicies.Settings`: General, Appearance, Playback, Notifications, Storage & cache, Privacy & diagnostics,
// About; P6 WK), and the SettingsCards of the open tab's sections (`SettingsPageProps.Grouping.PartsOf(Group)`), capped at 1000 (1200 at Huge) and stacking their
// control under the label below 560 content width. The tabs' bodies are `WinUiSettings.<Tab>.cs`; the theme picker and the options are `WinUiSettings.Theme.cs` (they
// are their own keys: the setup wizard mounts them too).
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
// needs a hook is an embedded child component. The body swaps with the page cross-fade (Q38, `WinUiSettingsRules.TabSwap`); only the scroller is keyed, per
// tab, so each tab keeps its own offset. The numbers are `Rules\WinUiSettingsRules.cs`.

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

namespace Wavee.Themes.WinUi;

/// <summary>Wavee Classic's Settings page.</summary>
internal static partial class WinUiSettings
{
    /// <summary>Registers the Settings page and the two Settings surfaces the setup wizard shares with it.</summary>
    internal static void Register(ViewRegistryBuilder views)
    {
        views.Add(ViewKeys.SettingsPage, Template, "winui.screens.settings");
        views.Add(ViewKeys.SettingsThemePicker, ThemePickerTemplate, "winui.screens.settings.theme-picker");
        views.Add(ViewKeys.SettingsThemeOptions, ThemeOptionsTemplate, "winui.screens.settings.theme-options");
    }

    /// <summary>The registered template of <see cref="ViewKeys.SettingsPage"/>.</summary>
    public static Element Template(SettingsPageProps props, in ViewContext ctx)
    {
        var hooks = ctx.Hooks;
        TierFacts tiers = ctx.Tier();
        // The page's tier record: read once, here, so the first frame is already in its final form; the tab bodies (static functions) and the components
        // that draw controls read it through `s_layout`, which every one of them sets from the same facts before it builds a control.
        var layout = s_layout = WinUiSettingsTiers.Of(tiers);

        // UNCONDITIONAL — above the tab switch (a conditional hook breaks hook ORDER).
        hooks.UseEffect(() =>
        {
            SeedControls();
            // Leaving Settings (unmount) tears down what only the visible Playback tab may own: the video-override flyout and its anchor.
            return (Action?)(static () => EnterPlayback(false));
        }, DepKey.Empty);
        // The route is keep-alive: a PARKED page is not unmounted, so the same teardown rides the deactivation edge.
        hooks.UseActivation(onDeactivated: static () => EnterPlayback(false));

        // The structure's grouping (P6 WK): the seven tabs over the twelve areas. A grouping the contract does not carry (a page mounted with the default) is
        // drawn the same way, one tab per area.
        var grouping = props.Grouping;
        int group = Math.Clamp(props.Group, 0, Math.Max(0, grouping.Count - 1));
        var parts = grouping.PartsOf(group);
        bool playback = HasTab(parts, Tab.Playback);
        hooks.UseEffect(() => EnterPlayback(playback), DepKey.From(playback ? 1 : 0));

        Element body = GroupBody(parts, props);

        string slug = grouping.Count > 0 ? grouping.Tabs[group].Slug : Settings.AreaSlugs[Math.Clamp(props.Area, 0, Settings.AreaSlugs.Length - 1)];
        // The body swaps with the page cross-fade: the lane is keyed per tab, so a switch is a real unmount the engine fades out under the arriving tab, and
        // each tab keeps its own offset. The title and the strip are SIBLINGS of the scroller: nothing compacts, sticks or gains a shadow.
        Element content = new BoxEl
        {
            Key = "settings:tab:" + slug, Direction = 1, Grow = 1f, MinHeight = 0f, Animate = WinUiSettingsRules.TabSwap,
            Children =
            [
                ScrollView(new BoxEl
                {
                    Direction = 1,
                    Padding = new Edges4(0f, 0f, 0f, layout.InsetX),
                    Children = [ContentColumn(body, layout, HasAside(layout, parts) ? ThemeAside() : null)],
                }) with { Grow = 1f, ScrollKey = "settings:" + slug, Key = "settings:scroll:" + slug },
            ],
        };

        return new BoxEl
        {
            Key = "settings:page", Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1, Gap = layout.PageGap,
            Padding = new Edges4(layout.InsetX, layout.InsetTop, layout.InsetX, 0f),
            Children = [Header(layout), TabStrip(layout, props), content],
        };
    }

    static bool HasTab(IReadOnlyList<Settings.Part> parts, Tab tab)
    {
        foreach (var part in parts)
            if (part.Tab == tab) return true;
        return false;
    }

    /// <summary>The Appearance tab at the tier that draws the preview column (Huge: W-Settings-Huge).</summary>
    static bool HasAside(WinUiSettingsLayout layout, IReadOnlyList<Settings.Part> parts)
        => layout.Aside && ContainsSection(parts, Tab.Appearance, "Theme");

    static bool ContainsSection(IReadOnlyList<Settings.Part> parts, Tab tab, string section)
    {
        foreach (var part in parts)
            if (part.Tab == tab && string.Equals(part.Section, section, StringComparison.Ordinal)) return true;
        return false;
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

    // ══ 1. THE SHARED ROW GRAMMAR ═══════════════════════════════════════════════════════════════════════════════════

    /// <summary>The page title: Title Large 40/52 at Standard and Huge, Title 28/36 at Compact and Small (W-Settings, -Small, -Compact). Keyed
    /// `winui.page.title` for the lint's inset edge; the line box is the stated line height at every tier so nothing under it moves.</summary>
    static Element Header(WinUiSettingsLayout layout)
        => new BoxEl
        {
            Key = TitleKey, Direction = 0, Shrink = 0f, MinWidth = 0f, AlignItems = FlexAlign.End,
            Children =
            [
                R.Type.PageHero(Loc.Get(Strings.Settings.Title)) with
                {
                    Size = layout.TitleSize, LineHeight = layout.TitleLine, Grow = 1f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                    LineStacking = LineStacking.BlockLineHeight,
                },
            ],
        };

    /// <summary>The page title's node (the lint and the tier tests find it).</summary>
    internal const string TitleKey = WinUiFrameLayout.PageTitleKey;

    /// <summary>The cards' column, capped by the tier's record (1000, 1200 at Huge, the whole width at Compact) and sitting on the page's left edge, never
    /// centred; at Huge the Appearance tab's preview column rides beside it.</summary>
    static Element ContentColumn(Element body, WinUiSettingsLayout layout, Element? aside)
    {
        float cap = layout.CapsColumn ? layout.ColumnMaxW : float.NaN;
        if (aside is null)
            return new BoxEl { Key = "settings:cards", Direction = 1, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, MaxWidth = cap, Children = [body] };
        return new BoxEl
        {
            Key = "settings:columns", Direction = 0, Gap = WinUiSettingsRules.AsideGap, AlignSelf = FlexAlign.Stretch, AlignItems = FlexAlign.Start, MinWidth = 0f,
            Children =
            [
                new BoxEl { Key = "settings:cards", Direction = 1, MinWidth = 0f, Basis = cap, Grow = 0f, Shrink = 1f, MaxWidth = cap, Children = [body] },
                aside.Value,
            ],
        };
    }

    /// <summary>The page's tier record as the tab bodies see it (see <see cref="Template"/>).</summary>
    static WinUiSettingsLayout s_layout = WinUiSettingsTiers.For(WidthTier.Standard);

    /// <summary>The tab strip (W-Settings: the seven tabs under the title, a hairline under the row). A SelectorBar in a sideways scroller: where the seven labels
    /// run past the page (Compact, Small) it scrolls and its end fades where more wait; elsewhere it ends at the column. The click opens the tab's first home
    /// area (`SettingsPageProps.OpenGroup`), so the area cell stays the page's one state. ONE tree at every tier: the strip rides its scroller always and the
    /// tier sets the scroller's height and fade, so a crossing keeps the SAME selector bar (its pill, its focus).</summary>
    static Element TabStrip(WinUiSettingsLayout layout, SettingsPageProps props)
    {
        var grouping = props.Grouping;
        var labels = new string[grouping.Count];
        var keys = new string?[grouping.Count];   // `settings:tabitem:<slug>`: a driver finds and clicks a tab by its slug
        for (int i = 0; i < labels.Length; i++)
        {
            labels[i] = Loc.Get(grouping.Tabs[i].TitleKey);
            keys[i] = "settings:tabitem:" + grouping.Tabs[i].Slug;
        }
        int group = Math.Clamp(props.Group, 0, Math.Max(0, labels.Length - 1));
        var style = layout.TabHeight > 0f ? new SelectorBarStyle { ItemHeight = layout.TabHeight } : null;
        Element bar = SelectorBar.Create(labels, new Signal<int>(group), onChange: props.OpenGroup, style: style, itemKeys: keys);
        return new BoxEl
        {
            Key = "settings:tabs", Direction = 1, Shrink = 0f, MinWidth = 0f,
            Children =
            [
                ScrollView(new BoxEl { Direction = 0, Children = [bar] }, horizontal: true) with
                {
                    Grow = 0f, Shrink = 0f, MinWidth = 0f, Height = layout.TabHeight > 0f ? layout.TabHeight : float.NaN, SuppressScrollBar = true,
                    AutoEdgeFade = layout.TabsScroll, ScrollKey = "settings:tabs",
                },
                Divider(),
            ],
        };
    }

    /// <summary>The tab's sections: one column of section headers over their cards, 4 DIP between cards. The tab's first child sits on the column's top edge: a
    /// section header that opens the tab drops its top space (the gap above a section separates it from the one before; the first has none).</summary>
    static Element TabStack(params Element[] children)
    {
        if (children.Length > 0 && children[0].Is(out BoxEl first))
        {
            if (first.Margin.Top > 0f) first = first with { Margin = first.Margin with { Top = 0f } };
            if (first.Padding.Top > 0f && first.Key is { } key && key.StartsWith(SectionKeyPrefix, StringComparison.Ordinal))
                first = first with { Padding = first.Padding with { Top = 0f } };
            children[0] = first;
        }
        return new BoxEl
        {
            Key = ColumnKey, Direction = 1, Gap = WinUiSettingsRules.CardSpacing, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
            Children = children,
        };
    }

    /// <summary>The tab body's column node: the tier tests find it.</summary>
    internal const string ColumnKey = "settings:column";

    /// <summary>The key every section header carries (the column's first header drops its space above).</summary>
    const string SectionKeyPrefix = "settings:section:";

    /// <summary>The key of a section header's caption.</summary>
    internal const string SectionCaptionKeyPrefix = "settings:section-caption:";

    /// <summary>A section header: Body Strong title over a 12-px secondary caption (W-Settings: 14 / 600 over 12), 2 apart; the record's space above (none above the
    /// tab's first, <see cref="TabStack"/>) and under is the header's own padding, so a crossing that changes it reveals it (`Design.Reveal.Resize`: the cards below
    /// ride it). The glyph the shared tab bodies pass is not drawn: WinUI's headers are words.</summary>
    static Element SectionHeader(string title, string? icon = null, string? subtitle = null)
    {
        var layout = s_layout;
        bool hasSub = subtitle is { Length: > 0 };
        Element text = new BoxEl
        {
            Direction = 1, Gap = Spacing.XXS, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, Grow = 1f, Basis = 0f,
            Children = hasSub
                ?
                [
                    BodyStrong(title) with { MinWidth = 0f },
                    new BoxEl
                    {
                        Key = SectionCaptionKeyPrefix + title, Direction = 1, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
                        Children = [Caption(subtitle!) with { Color = Tok.TextSecondary, MinWidth = 0f, Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.WordEllipsis }],
                    },
                ]
                : [BodyStrong(title) with { MinWidth = 0f }],
        };
        return new BoxEl
        {
            Key = SectionKeyPrefix + title,
            Direction = 0, AlignItems = FlexAlign.Start,
            Padding = new Edges4(0f, layout.SectionTop, 0f, layout.SectionBottom),
            Animate = Design.Reveal.Resize,
            AlignSelf = FlexAlign.Stretch,
            Children = [text],
        };
    }

    /// <summary>One settings row (a <see cref="SettingsCard"/>). Inert under the pointer unless click-enabled. WinUI's rows are words and a control: the
    /// header glyph of the shared tab bodies is not drawn (the three presentation cards, <see cref="IconRow"/>, keep theirs).</summary>
    static Element Row(string label, string? sub, Element? control = null, string? icon = null,
                       SettingsCard.ContentAlignment align = SettingsCard.ContentAlignment.Right,
                       bool isClickEnabled = false, Action? onClick = null, bool isEnabled = true, string? actionIcon = null)
        => SettingsCard.Create(new SettingsCard.Options
        {
            Header = label,
            Description = sub,
            HeaderIcon = null,
            Content = control,
            Alignment = Aligned(align, control),
            Style = InlineStyle(control),
            IsClickEnabled = isClickEnabled,
            IsActionIconVisible = isClickEnabled,
            ActionIcon = actionIcon,
            OnClick = onClick,
            IsEnabled = isEnabled,
        });

    /// <summary>A row with its 20-px glyph (the Theme, App mode and Zoom cards of W-Settings).</summary>
    static Element IconRow(string label, string? sub, string icon, Element? control = null,
                           SettingsCard.ContentAlignment align = SettingsCard.ContentAlignment.Right)
        => SettingsCard.Create(new SettingsCard.Options
        {
            Header = label,
            Description = sub,
            HeaderIcon = icon,
            Content = control,
            Alignment = Aligned(align, control),
            Style = InlineStyle(control),
        });

    /// <summary>One expander item (thresholds 0: never wraps, never drops its glyph).</summary>
    static Element Item(string label, string? sub, Element? control = null,
                        SettingsCard.ContentAlignment align = SettingsCard.ContentAlignment.Right,
                        bool isEnabled = true, bool isClickEnabled = false, Action? onClick = null, string? icon = null)
        => SettingsExpander.Item(label, sub, control, Aligned(align, control), isEnabled, isClickEnabled, onClick, null);

    /// <summary>A toggle's row at a tier that stacks the other controls keeps the board's label | state | switch row: the card's own wrap thresholds (it moves a
    /// control under its label when it measures narrower than 476) are switched off for it. Null elsewhere: the stock card.</summary>
    static SettingsCard.Style? InlineStyle(Element? control)
        => s_layout.StacksControls && IsToggle(control) ? SettingsCard.DefaultStyle with { WrapThreshold = 0f, WrapNoIconThreshold = 0f } : null;

    /// <summary>The alignment a row's control gets in this tier: where the cards stack (under 560 content width) a control sits UNDER its label, full width,
    /// instead of squeezing beside it — except a toggle, which keeps the label | state | switch row of the board. Asked for explicitly, not left to the card's own
    /// measured threshold (that one reads the card's width a frame late, so the first frame would be the wide form).</summary>
    static SettingsCard.ContentAlignment Aligned(SettingsCard.ContentAlignment align, Element? control)
        => align == SettingsCard.ContentAlignment.Right && s_layout.StacksControls && control is not null && !IsToggle(control)
            ? SettingsCard.ContentAlignment.Vertical
            : align;

    /// <summary>N4: a collapsed picker group's ANSWER in its header — 14 DIP secondary, one line, char-ellipsis.</summary>
    static Element ValueTag(string value) => new TextEl(value)
    {
        Size = WinUiSettingsRules.ValueTagSize, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
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
        return Row(Loc.Get(row.TitleKey), description, control, null,
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
