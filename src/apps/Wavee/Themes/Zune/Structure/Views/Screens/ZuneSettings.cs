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
// SECTIONS. The core page state is the twelve-AREA cell (`SettingsPageProps.AreaCell`; the deep links, `Settings.Open` and a theme pick's "open on Appearance" all name an
// area, `Settings.Areas.cs`), so the list is the board's twelve words: account, appearance, playback, audio, lyrics, devices & connect, storage, notifications, keyboard,
// privacy, feedback & help, about. Every area is a list of document sections (`Settings.PartsOf`), each a group in its column; `ZuneSettings.Areas.cs` builds the four that
// the document gained (account, devices, keyboard, feedback) and composes the area from the tabs' groups.
//
// NODE IDENTITY. The list, the lane, a group and a row are the same nodes in every tier: a crossing changes their DIRECTION, widths and wrap (live props), never
// their tree, so nothing remounts and the engine's reflow carries them to their new places (`ZuneMotion`). Only the area's lane is keyed, per area, so a
// switch is the page cross-fade and each area keeps its own scroll offset.

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

    /// <summary>One group of a tab, named by the document section it draws (an area is a list of such sections).</summary>
    internal readonly record struct Named(string Section, Element El, bool Sentence = false);

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
        int area = Math.Clamp(props.Area, 0, Settings.AreaSlugs.Length - 1);
        string slug = Settings.AreaSlugs[area];
        string[] names = SectionNames();

        // Compact (Z-Settings-Sections-Compact, Z-Settings-Playback-Compact): the page is the sections list, and a section opens as its own page under a "‹ settings"
        // crumb. The open section is the ROUTE's (`props.OpenSection`: Back, Escape and the mouse's back button close it, a deep link opens it, and a resize across the Compact edge keeps
        // it: Small shows the same section in the column layout, Compact its page again); the tab itself is still the core's pivot cell.
        bool compact = lay.ListIsStrip;
        bool listPage = compact && !props.OpenSection;
        bool sectionPage = compact && props.OpenSection;

        Element[] groups = AreaGroups(k, (Settings.Area)area, props);

        if (!compact) groups = [SectionHeading(names[area]), .. groups];

        // The tab's column: every group of the tab, capped at the tier's width and sitting on the page's left edge (never centred).
        Element column = new BoxEl
        {
            Key = "zs:column", Direction = 1, Gap = Z.SectionGap, AlignSelf = FlexAlign.Start, MinWidth = 0f,
            Basis = lay.ColumnW, MaxWidth = lay.ColumnW, Grow = lay.ColumnGrows ? 1f : 0f, Shrink = 1f,
            Animate = ZuneMotion.ColumnCollapse, Children = groups,
        };
        var scrolled = new List<Element>(2) { column };
        if (lay.ShowsAside && (Settings.Area)area == Settings.Area.Appearance) scrolled.Add(Aside(lay));
        else if (lay.ShowsAside && (Settings.Area)area == Settings.Area.Feedback) scrolled.Add(HoldsAside(k));

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

        Element[] bodyKids = listPage ? [SectionsPage(props, names)] : compact ? [lane] : [Sections(k, props, area), lane];
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
            Children = sectionPage ? [CrumbRow(props.CloseSection), PanoramaTitle(lay, names[area]), body] : [PanoramaTitle(lay, null), body],
        }, lay.Gutter);
    }

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
            Children = [ZuneText.Cased(title, LabelKind.Heading) with { MaxLines = wraps ? Z.SectionTitleLines : 1, Wrap = wraps ? TextWrap.WrapWholeWords : TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Margin = new Edges4(0f, lay.TitleTop, 0f, 0f) }],
        };
    }

    /// <summary>The open section's heading at the top of its column (Z-Settings-Playback: 40 / 48, light).</summary>
    static Element SectionHeading(string name) => new BoxEl
    {
        Key = "zs:heading", Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
        Children = [ZuneText.Title(name) with { MaxLines = Z.SectionTitleLines, Trim = TextTrim.CharacterEllipsis }],
    };

    /// <summary>The Compact sections page: every section a large light word with a chevron, one 56-tall target each; a tap opens the section's page.</summary>
    static Element SectionsPage(SettingsPageProps props, string[] names)
    {
        var rows = new Element[names.Length];
        for (int i = 0; i < rows.Length; i++)
        {
            int at = i;
            rows[i] = new BoxEl
            {
                Key = "zs:link:" + Settings.AreaSlugs[i], Direction = 0, AlignItems = FlexAlign.Center, Gap = Z.SectionRowGap, MinWidth = 0f, MinHeight = Z.SectionRowH,
                Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
                OnClick = () => props.OpenArea(at),
                Children =
                [
                    ZuneText.Heading(names[i]) with { Color = Tok.TextPrimary, HoverColor = Tok.AccentTextPrimary, Grow = 1f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    new BoxEl { Shrink = 0f, Children = [Icon(Icons.ChevronRight, Z.SectionChevron, Tok.TextTertiary)] },
                ],
            }.Interactive(Interaction.Subtle);
        }
        // The list scrolls like a section's lane: twelve 56-tall rows overrun a short window, and the last ones must still come up from under the play bar.
        return ScrollView(new BoxEl
        {
            Key = SectionsKey, Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch, Padding = new Edges4(0f, 0f, 0f, Z.LaneBottom), Children = rows,
        }) with { Grow = 1f, Shrink = 1f, MinHeight = 0f, AlignSelf = FlexAlign.Stretch, ScrollKey = "zs:sections", Key = "zs:scroll:sections" };
    }

    /// <summary>The "‹ settings" crumb over a Compact section's page: one 40-tall target back to the sections.</summary>
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

    /// <summary>The twelve sections' names, in the order of <see cref="Settings.Area"/>.</summary>
    static string[] SectionNames()
    {
        var names = new string[Settings.AreaTitleKeys.Length];
        for (int i = 0; i < names.Length; i++) names[i] = Loc.Get(Settings.AreaTitleKeys[i]);
        return names;
    }

    /// <summary>The section list: a column of words beside the lane (Small and up), a wrapping row of the same words above it (Compact). ONE node, one
    /// child per tab; the direction, wrap, width and gap are the tier's. The open section is the ink in the heavier weight, the rest tertiary.</summary>
    static Element Sections(ZuneKit k, SettingsPageProps props, int open)
    {
        var lay = k.Lay;
        string[] names = SectionNames();
        var cell = props.AreaCell();
        var items = new Element[names.Length];
        for (int i = 0; i < items.Length; i++)
        {
            int at = i;
            bool on = i == open;
            items[i] = new BoxEl
            {
                Key = "zs:link:" + Settings.AreaSlugs[i], Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f, MinWidth = 0f,
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
