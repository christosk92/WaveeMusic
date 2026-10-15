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
                    Padding = new Edges4(Spacing.PageWide, Spacing.L, Spacing.PageWide, Spacing.PageWide),
                    Children = [ContentColumn(body)],
                }) with { Grow = 1f, ScrollKey = "settings:" + slug, Key = "settings:scroll:" + slug },
            ],
        };

        // W28: the masthead and the strip are SIBLINGS of the scroller — nothing compacts, sticks or gains a shadow.
        return new BoxEl
        {
            Grow = 1f, Direction = 1,
            Children =
            [
                Header(),
                new BoxEl
                {
                    Direction = 1, Padding = new Edges4(Spacing.PageWide, 0f, Spacing.PageWide, 0f),
                    Children = [SelectorBar.Create(TabLabels(), props.TabCell()), Divider()],
                },
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

    /// <summary>The section header's margin: a 32-DIP gap above, the 8-DIP breath under.</summary>
    static Edges4 SectionHeaderMargin => new(0f, Spacing.XXXL, 0f, Spacing.S);

    /// <summary>The page head: the page title alone (40/52 display, no glyph and no meta line), padded (36, 16, 36, 12).</summary>
    static Element Header() => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.End,
        Padding = new Edges4(Spacing.PageWide, Spacing.L, Spacing.PageWide, Spacing.M),
        Children = [R.Type.SurfaceDisplay(Loc.Get(Strings.Settings.Title)) with { Key = "settings:title", Grow = 1f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f }],
    };

    /// <summary>N1: a LEFT-aligned 1000-DIP column — never centred, never full-bleed.</summary>
    static Element ContentColumn(Element body) => new BoxEl
    {
        Direction = 1, MaxWidth = ClassicSettingsRules.ContentMaxWidth, AlignSelf = FlexAlign.Stretch, Children = [body],
    };

    /// <summary>N2: cards 4 DIP apart. The tab's first child sits on the column's top edge: a section header that opens the tab drops its
    /// 32-DIP top margin (the gap above a section separates it from the one before; the first has none).</summary>
    static Element TabStack(params Element[] children)
    {
        if (children.Length > 0 && children[0] is BoxEl { Margin.Top: > 0f } first)
            children[0] = first with { Margin = first.Margin with { Top = 0f } };
        return new BoxEl
        {
            Direction = 1, Gap = ClassicSettingsRules.CardSpacing, AlignSelf = FlexAlign.Stretch, Children = children,
        };
    }

    /// <summary>A group eyebrow: 16-DIP glyph + BodyStrong title, optionally a ≤ 2-line caption in the SAME column as
    /// the title; margin (0, 32, 0, 8). One line centres on the glyph; two lines hang from the top.</summary>
    static Element SectionHeader(string title, string? icon = null, string? subtitle = null)
    {
        bool hasSub = subtitle is { Length: > 0 };
        Element text = hasSub
            ? new BoxEl
            {
                Direction = 1, Gap = Spacing.XXS, Grow = 1f, Basis = 0f, MinWidth = 0f,
                Children =
                [
                    BodyStrong(title),
                    Caption(subtitle!) with { Color = Tok.TextSecondary, MinWidth = 0f, Wrap = TextWrap.Wrap, MaxLines = 2 },
                ],
            }
            : BodyStrong(title);

        return new BoxEl
        {
            Key = "settings:section:" + title,
            Direction = 0, Gap = Spacing.S,
            AlignItems = hasSub ? FlexAlign.Start : FlexAlign.Center,
            Margin = SectionHeaderMargin,
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
            Alignment = align,
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
        => SettingsExpander.Item(label, sub, control, align, isEnabled, isClickEnabled, onClick, icon);

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
        => ToggleSwitch.Create(new Signal<bool>(row.Current is true), onChange: on =>
        {
            if (row.Writer.Toggle!(on)) Settings.Bump();
        }, isEnabled: isEnabled, style: SettingsCard.CompactToggleStyle());

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
