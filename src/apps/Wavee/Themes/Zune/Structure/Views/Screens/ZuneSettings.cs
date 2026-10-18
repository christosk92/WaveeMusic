// ── Themes/Zune/Structure/Views/Screens/ZuneSettings.cs — Zune's `Pages.Settings` (themes plan 6.3, P5 Z8b) ──────────────────────────────────
//
// Boards: Z-Settings-Appearance, Z-Settings-Playback, Z-Settings-Sections, Z-Theme-Options (Standard; -Compact, -Small and -Huge for the sizes ZT8 tunes later).
//
// THE PICTURE. A panorama title ("settings", 120/130 at the light weight, 64/72 under the compact width), then a lowercase SECTION LIST on the left (220,
// the 20/28 display voice, the open section in the ink and heavier, the rest tertiary) beside the open section's column (660, every group a 26/32 heading
// over its label | control rows); on Appearance a PREVIEW of the options beside the column (or under it where there is no room). Where the tier has no room for a
// list beside the column (Compact) the same words sit in a wrapping row of 40-tall targets above it; the sizes of the title, the list's words, the label column, the picker and
// the swatches are the tier's record (`ZuneSettingsTiers`, tuned by ZT8 against the -Compact, -Small and -Huge boards).
//
// WHAT STAYS CORE. The document (`SettingsDoc`: which rows exist, their titles, values, enablement and reasons, visibility), every verb (`row.Writer`), the facts
// the rows ask (`SettingsFacts`), the page epoch (`Settings.Bump`), the Storage census (the props carry it), the pivot's root-lifetime cell and the deep link,
// and the page context the verbs read. This view reads the document and writes through each row's writer; it never touches the settings store (WVTH002).
//
// SECTIONS. The core page state is the seven-tab cell (`SettingsPageProps.TabCell`; the deep links, `Settings.Open` and a theme pick's "open on Appearance"
// all name a tab), so the list is the seven tabs and every document section of the open tab is a group in its column. The board lists twelve
// sections (account, appearance, playback, audio, lyrics, devices, downloads & storage, notifications, keyboard, privacy, feedback & help, about); each is
// reachable here as a group of one tab (audio under playback, lyrics under appearance, feedback & help under about and privacy) except the account, the
// devices and the keyboard sections, which have no document rows (the account is the account menu; the devices are the player bar's picker).
//
// NODE IDENTITY. The list, the lane, a group and a row are the same nodes in every tier: a crossing changes their DIRECTION, widths and wrap (live props), never
// their tree, so nothing remounts and the engine's reflow carries them to their new places (`ZuneMotion`). Only the tab's lane is keyed, per tab, so a tab
// switch is the page cross-fade and each tab keeps its own scroll offset.

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
using Z = Wavee.Themes.Zune.ZuneSettingsRules;

namespace Wavee.Themes.Zune;

/// <summary>Zune's Settings page.</summary>
internal static partial class ZuneSettings
{
    /// <summary>Registers the Settings page and the two Settings surfaces the setup wizard shares with it.</summary>
    internal static void Register(ViewRegistryBuilder views)
    {
        views.Add(ViewKeys.SettingsPage, Template, "zune.screens.settings");
        views.Add(ViewKeys.SettingsThemePicker, ThemePickerTemplate, "zune.screens.settings.theme-picker");
        views.Add(ViewKeys.SettingsThemeOptions, ThemeOptionsTemplate, "zune.screens.settings.theme-options");
    }

    /// <summary>The page title's node (the tier tests find it).</summary>
    internal const string TitleKey = "zs:title";

    /// <summary>The section list's node.</summary>
    internal const string SectionsKey = "zs:sections";

    /// <summary>The node key of the open tab's lane.</summary>
    internal static string LaneKey(string slug) => "zs:tab:" + slug;

    /// <summary>The registered template of <see cref="ViewKeys.SettingsPage"/>.</summary>
    public static Element Template(SettingsPageProps props, in ViewContext ctx)
    {
        TierFacts tiers = ctx.Tier();
        var lay = ZuneSettingsTiers.Of(tiers);
        var k = new ZuneKit(lay);
        int tab = Math.Clamp(props.Tab, 0, Settings.TabSlugs.Length - 1);
        string slug = Settings.TabSlugs[tab];
        string[] names = SectionNames();

        // Compact (Z-Settings-Sections-Compact, Z-Settings-Playback-Compact): the page is the sections list, and a section opens as its own page under a "‹ settings"
        // crumb. The open state is the view's (a fresh mount of the page is the list); the tab itself is still the core's pivot cell, so every deep link keeps working.
        var openSection = ctx.Hooks.UseSignal(false);
        bool compact = lay.ListIsStrip;
        bool listPage = compact && !openSection.Value;
        bool sectionPage = compact && openSection.Value;

        Element[] groups = (Settings.Tab)tab switch
        {
            Settings.Tab.Appearance => AppearanceTab(k),
            Settings.Tab.Playback => PlaybackTab(k),
            Settings.Tab.Notifications => NotificationsTab(k),
            Settings.Tab.Storage => StorageTab(k, props.Storage),
            Settings.Tab.PrivacyDiagnostics => PrivacyTab(k),
            Settings.Tab.About => AboutTab(k),
            _ => GeneralTab(k),
        };

        if (!compact) groups = [SectionHeading(names[tab]), .. groups];

        // The tab's column: every group of the tab, capped at the tier's width and sitting on the page's left edge (never centred).
        Element column = new BoxEl
        {
            Key = "zs:column", Direction = 1, Gap = Z.SectionGap, AlignSelf = FlexAlign.Start, MinWidth = 0f,
            Basis = lay.ColumnW, MaxWidth = lay.ColumnW, Grow = lay.ColumnGrows ? 1f : 0f, Shrink = 1f,
            Animate = ZuneMotion.ColumnCollapse, Children = groups,
        };
        var scrolled = new List<Element>(2) { column };
        if (lay.ShowsAside && (Settings.Tab)tab == Settings.Tab.Appearance) scrolled.Add(Aside(lay));

        Element lane = new BoxEl
        {
            Key = LaneKey(slug), Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, MinHeight = 0f, Animate = Z.TabSwap,
            Children =
            [
                ScrollView(new BoxEl
                {
                    Direction = 0, Wrap = true, Gap = Z.AsideGap, MinWidth = 0f, AlignItems = FlexAlign.Start,
                    Padding = new Edges4(0f, Z.LaneTop, 0f, Z.LaneBottom), Children = scrolled.ToArray(),
                }) with { Grow = 1f, Shrink = 1f, MinHeight = 0f, ScrollKey = "zs:" + slug, Key = "zs:scroll:" + slug },
            ],
        };

        Element[] bodyKids = listPage ? [SectionsPage(props, names, openSection)] : compact ? [lane] : [Sections(k, props, tab), lane];
        Element body = new BoxEl
        {
            Key = "zs:body", Direction = (byte)(lay.ListIsStrip ? 1 : 0), Gap = lay.BodyGap, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
            Padding = new Edges4(0f, Z.TitleBodyGap, 0f, 0f), AlignItems = FlexAlign.Stretch,
            Children = bodyKids,
        };

        // The page column (A1): capped at 2400 + the gutters and centred, so the title and the body share the band's x at every width.
        return ZuneFrameRules.PageRow(new BoxEl
        {
            Key = "zs:page", Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
            Children = sectionPage ? [CrumbRow(openSection), PanoramaTitle(lay, names[tab]), body] : [PanoramaTitle(lay, null), body],
        }, lay.Gutter);
    }

    // ── the title ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The panorama title: the largest voice at the light weight, bled to the left of the gutter by its optical inset; its box reserves the larger
    /// size's line (the title scales from its left edge when the tier steps, so nothing under it moves).</summary>
    static Element PanoramaTitle(ZuneSettingsLayout lay, string? section = null)
    {
        string text = section ?? Loc.Get(Strings.Settings.Title);
        TextEl title = lay.CompactTitle ? R.Type.PanoramaCompact(text) : R.Type.Panorama(text);
        if (lay.TitleSize > 0f) title = title with { Size = lay.TitleSize, LineHeight = lay.TitleLineH };   // Huge: the board's 150 over its 160 line
        // The line box is exactly the stated line height (the board's CSS line), set `TitleTop` over the body's start; the descenders hang below it.
        title = title with { LineStacking = LineStacking.BlockLineHeight };
        return new BoxEl
        {
            Key = TitleKey, Direction = 0, Shrink = 0f, MinWidth = 0f, Height = section is null ? lay.TitleH : float.NaN, Animate = ZuneMotion.TitleScale,
            // The page column pads the gutter; the panorama bleeds left of it by its optical inset (never past the column's edge).
            Margin = new Edges4(-MathF.Min(lay.Gutter, Z.PanoramaBleed), 0f, 0f, 0f),
            Children = [ZuneText.Cased(title, LabelKind.Heading) with { MaxLines = section is null ? 1 : Z.SectionTitleLines, Wrap = section is null ? TextWrap.NoWrap : TextWrap.WrapWholeWords, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Margin = new Edges4(0f, lay.TitleTop, 0f, 0f) }],
        };
    }

    /// <summary>The open section's heading at the top of its column (Z-Settings-Playback: 40 / 48, light).</summary>
    static Element SectionHeading(string name) => new BoxEl
    {
        Key = "zs:heading", Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
        Children = [ZuneText.Title(name) with { MaxLines = Z.SectionTitleLines, Trim = TextTrim.CharacterEllipsis }],
    };

    /// <summary>The Compact sections page: every section a large light word with a chevron, one 56-tall target each; a tap opens the section's page.</summary>
    static Element SectionsPage(SettingsPageProps props, string[] names, Signal<bool> open)
    {
        var cell = props.TabCell();
        var rows = new Element[names.Length];
        for (int i = 0; i < rows.Length; i++)
        {
            int at = i;
            rows[i] = new BoxEl
            {
                Key = "zs:link:" + Settings.TabSlugs[i], Direction = 0, AlignItems = FlexAlign.Center, Gap = Z.SectionRowGap, MinWidth = 0f, MinHeight = Z.SectionRowH,
                Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
                OnClick = () => { cell.Value = at; open.Value = true; },
                Children =
                [
                    ZuneText.Heading(names[i]) with { Color = Tok.TextPrimary, HoverColor = Tok.AccentTextPrimary, Grow = 1f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    new BoxEl { Shrink = 0f, Children = [Icon(Icons.ChevronRight, Z.SectionChevron, Tok.TextTertiary)] },
                ],
            }.Interactive(Interaction.Subtle);
        }
        return new BoxEl { Key = SectionsKey, Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch, AlignSelf = FlexAlign.Stretch, Children = rows };
    }

    /// <summary>The "‹ settings" crumb over a Compact section's page: one 40-tall target back to the sections.</summary>
    static Element CrumbRow(Signal<bool> open) => new BoxEl
    {
        Key = "zs:crumb", Direction = 0, AlignItems = FlexAlign.Center, Gap = Z.SectionRowGap, Shrink = 0f, MinWidth = 0f, MinHeight = Z.CrumbH, AlignSelf = FlexAlign.Start,
        Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = () => open.Value = false,
        Children =
        [
            Icon(Icons.ChevronLeft, Z.SectionChevron, Tok.TextSecondary),
            ZuneText.Meta(Loc.Get(Strings.Settings.Title)) with { HoverColor = Tok.TextPrimary },
        ],
    }.Interactive(Interaction.Subtle);

    // ── the section list ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The seven tabs' names, in the order of <see cref="Settings.Tab"/>.</summary>
    static string[] SectionNames() =>
    [
        Loc.Get(Strings.Settings.Tabs.General),
        Loc.Get(Strings.Settings.Tabs.Appearance),
        Loc.Get(Strings.Settings.Tabs.Playback),
        Loc.Get(Strings.Settings.Notify.Title),
        Loc.Get(Strings.Settings.Tabs.Storage),
        Loc.Get(Strings.Settings.Tabs.Privacy),
        Loc.Get(Strings.Settings.Tabs.About),
    ];

    /// <summary>The section list: a column of words beside the lane (Small and up), a wrapping row of the same words above it (Compact). ONE node, one
    /// child per tab; the direction, wrap, width and gap are the tier's. The open section is the ink in the heavier weight, the rest tertiary.</summary>
    static Element Sections(ZuneKit k, SettingsPageProps props, int open)
    {
        var lay = k.Lay;
        string[] names = SectionNames();
        var cell = props.TabCell();
        var items = new Element[names.Length];
        for (int i = 0; i < items.Length; i++)
        {
            int at = i;
            bool on = i == open;
            items[i] = new BoxEl
            {
                Key = "zs:link:" + Settings.TabSlugs[i], Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f, MinWidth = 0f,
                MinHeight = lay.ListItemH, Padding = new Edges4(0f, 0f, lay.ListItemPadRight, 0f), Animate = ZuneMotion.PivotSlide,
                Role = AutomationRole.Tab, Focusable = true, Cursor = CursorId.Hand, OnClick = () => cell.Value = at,
                Children =
                [
                    ZuneText.Nav(names[i]) with
                    {
                        Color = on ? Tok.TextPrimary : Tok.TextTertiary, HoverColor = Tok.TextPrimary, Weight = on ? Z.NavSelectedWeight : Z.NavRestWeight,
                        Size = lay.NavSize, LineHeight = lay.NavLineH,
                    },
                ],
            }.Interactive(Interaction.Subtle);
        }
        // ONE scroller at every tier (the node never remounts on a crossing): the words are one line in a horizontal scroller whose end dissolves where more sections
        // wait (Compact: seven words never wrap into three lines); beside the lane the column of words fits it and nothing scrolls, so nothing fades.
        Element words = new BoxEl { Direction = (byte)(lay.ListIsStrip ? 0 : 1), Gap = lay.ListGap, MinWidth = lay.ListIsStrip ? 0f : lay.ListW, AlignItems = FlexAlign.Stretch, Children = items };
        return new BoxEl
        {
            Key = SectionsKey, Direction = 1, Width = lay.ListIsStrip ? float.NaN : lay.ListW, Shrink = 0f, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
            AlignItems = FlexAlign.Stretch, Animate = ZuneMotion.ColumnCollapse,
            Children =
            [
                ScrollView(words, horizontal: true) with
                {
                    Key = "zs:sections:scroll", Grow = 0f, ContentSized = true, MinWidth = 0f, AutoEdgeFade = true, AutoEdgeFadeBand = Z.NavFade,
                    SuppressScrollBar = true, ScrollKey = "zs:sections",
                },
            ],
        };
    }
}
