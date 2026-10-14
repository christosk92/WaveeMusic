// ── Themes/Classic/Structure/Views/Screens/ClassicSettings.Theme.cs — the theme picker and the active theme's options (themes plan 8.1-8.2, T1b; T11b) ──
//
// From `Screens\Settings.UI.Theme.cs`. Settings › Appearance, the theme half: the THEME PICKER (one card per installed theme, driven by `ThemePickerFacts`) and
// the ACTIVE THEME'S OPTIONS (one expander: "‹Theme› options", its rows grouped; the grouping, the visibility and the legacy rows a structure still binds are the
// core's `SettingsThemeOptions`, this file draws them). The setup wizard's Theme step draws the same two views through their own keys
// (`Settings.ThemePicker`, `Settings.ThemeOptions`); the sidebar layout is a generated Cards row of the options (M7a), drawn with the preview cards of
// `ClassicSettings.Appearance.cs`.
//
// WHAT THIS FILE NEVER DOES: name a theme. The picker draws `ThemePickerEntry` data; the options expander draws whatever `SettingsOptionsView` holds (a theme
// with no options gets one quiet row; one with only legacy rows gets them). A legacy row is drawn by its SettingsDoc id (`LegacyItem`): the id is the theme's,
// the control is the page's.
//
// NO LAYOUT JUMPS. A picker card has one fixed size whatever its text (name: one line, description: two, ellipsised) and whatever its preview (a miniature when
// the preview asset is missing, the image over it when present). When a row appears or disappears (the rail rows follow the page layout, a `VisibleWhen`
// option follows its condition) the group around it tweens its height, so everything below eases instead of snapping; an expander opens and closes on the
// engine's own motion. Rows carry no Enter/Exit of their own: the engine seeds an Enter on every MOUNT, which would fade the whole tab in on each tab switch
// (production swaps tab bodies with no transition).
//
// FROZEN CONTROL INPUTS. A ComboBox freezes its items, signal and onChange at mount, a Slider its signal: the option combo, slider and colour picker are
// therefore tiny components that keep one stable signal in step with the live option value through an effect (`GeneratedOptionRow.Live`), exactly as the zoom and
// player-style combos do.

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
using Tab = Wavee.Settings.Tab;

namespace Wavee.Themes.Classic;

internal static partial class ClassicSettings
{
    // ══ 0. THE KEYED VIEWS ════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.SettingsThemePicker"/>.</summary>
    public static Element ThemePickerTemplate(SettingsThemePickerProps props, in ViewContext ctx) => ThemePickerView();

    /// <summary>The registered template of <see cref="ViewKeys.SettingsThemeOptions"/>: the page's expander, or the wizard's plate rows (an empty
    /// element while the theme has nothing to pick there; the wizard step does not place it then).</summary>
    public static Element ThemeOptionsTemplate(SettingsThemeOptionsProps props, in ViewContext ctx)
        => props.Wizard ? WizardThemeOptions() ?? new BoxEl() : ThemeOptionsExpander();

    // ══ 1. THE THEME PICKER ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A picker card's footprint. The 1-DIP inset is the resting padding a selected card spends on its border growing
    /// 1 → 2, so the thumbnail never shifts by a pixel when the selection moves.</summary>
    static readonly Controls.PickerShell ThemeCardShell = new(ClassicSettingsRules.ThemeCardWidth, ClassicSettingsRules.ThemeCardHeight, 1f, 0f);

    /// <summary>The picker as a Settings row: a card holding the strip of theme cards. With one installed theme it is one card,
    /// left-aligned, exactly as tall as with five; with more than fit a line the strip wraps to the next line.</summary>
    static Element ThemePickerView() => Embed.Comp(static () => new ThemePickerCard());

    sealed class ThemePickerCard : Component
    {
        public override Element Render()
        {
            var entries = ThemePickerFacts.State.Value.Entries(Platform.Developer.Enabled.Value);   // tracked: a switch or Developer mode re-renders
            Element body;
            if (entries.Count == 0)
            {
                body = Caption(Loc.Get(Strings.Settings.ThemePicker.None)) with { Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MinWidth = 0f };
            }
            else
            {
                int selected = -1;
                for (int i = 0; i < entries.Count; i++)
                    if (entries[i].Selected) { selected = i; break; }
                body = Controls.PickerStrip(entries.Count, selected, (i, on) => ThemeCard(entries[i], on), PickTheme);
            }

            var card = SettingsCard.DefaultStyle;
            return new BoxEl
            {
                Key = "settings:theme:picker", Direction = 1, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
                Padding = Edges4.All(Spacing.L), Corners = CornerRadius4.All(card.CornerRadius), Fill = card.Background,
                BorderWidth = card.BorderWidth, BorderColor = card.Border,
                Children = [body],
            };
        }
    }

    /// <summary>The pick. The picked theme's root mounts fresh (owner Q26: nothing carried), so the Settings page opens again ON Appearance, with the
    /// picker as its first row, instead of on General.</summary>
    static void PickTheme(int index)
    {
        if (SettingsVerbs.ThemeIndex() == index) return;
        Settings.OpenNextMountOn(Tab.Appearance);
        if (D(Tab.Appearance, "themePicker").Writer.Choose!(index)) Settings.Bump();
    }

    static Element ThemeCard(ThemePickerEntry entry, bool on)
    {
        string name = Loc.Get(entry.NameKey);
        var nameText = (on ? BodyStrong(name) : Body(name)) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Wrap = TextWrap.NoWrap, MinWidth = 0f, Shrink = 1f };
        var tags = new List<Element>(3) { nameText };
        if (on) tags.Add(ActiveTag());
        if (entry.Availability == ThemeAvailability.Developer) tags.Add(DeveloperTag());

        var kids = new List<Element>(2)
        {
            new BoxEl
            {
                Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
                Children = tags.ToArray(),
            },
        };
        if (entry.DescriptionKey is { } descriptionKey)
            kids.Add(Caption(Loc.Get(descriptionKey)) with
            {
                Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.WordEllipsis, MinWidth = 0f,
                AlignSelf = FlexAlign.Stretch,
            });

        Element details = new BoxEl
        {
            Direction = 1, Gap = Spacing.XXS, Grow = 1f, MinWidth = 0f, MinHeight = 0f, ClipToBounds = true,
            Padding = new Edges4(Spacing.M, Spacing.S, Spacing.M, Spacing.M),
            Children = kids.ToArray(),
        };
        return Controls.PickerCard(on, ThemeCardShell, ThemeThumbnail(entry.Preview), details) with { Key = "settings:theme:card:" + entry.Id };
    }

    static Element DeveloperTag() => new BoxEl
    {
        Height = Spacing.L, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f), Corners = Radii.PillAll, Fill = Tok.FillSubtleSecondary,
        Children = [new TextEl(Loc.Get(Strings.Settings.ThemePicker.Developer)) { Size = ClassicSettingsRules.TagSize, Weight = 600, Color = Tok.TextSecondary, MaxLines = 1 }],
    };

    /// <summary>The preview: the theme's preview image over a UI-native miniature (so a missing asset, the state of every theme
    /// until it ships one, leaves a thumbnail of the same size, and the image fades in over it instead of replacing a blank).</summary>
    static Element ThemeThumbnail(ThemePreview preview)
    {
        var layers = new List<Element>(2) { ThemeMiniature() };
        string asset = R.IsLight ? preview.LightAsset : preview.DarkAsset;
        if (PreviewPath(asset) is { } path)
            layers.Add(new ImageEl
            {
                Source = path, Fit = ImageFit.Cover, Height = ClassicSettingsRules.ThemeThumbHeight, AlignSelf = FlexAlign.Stretch,
                DecodePx = ClassicSettingsRules.ThemeCardWidth * 2f, Placeholder = ColorF.Transparent,
                RevealTransition = ImageTransition.Fade(ClassicSettingsRules.PreviewFadeMs),
            });
        return new BoxEl
        {
            ZStack = true, Height = ClassicSettingsRules.ThemeThumbHeight, Shrink = 0f, AlignSelf = FlexAlign.Stretch, ClipToBounds = true,
            Fill = Tok.FillSolidBase, Children = layers.ToArray(),
        };
    }

    static readonly Dictionary<string, string?> s_previewPaths = new(StringComparer.Ordinal);

    /// <summary>The full path of a preview asset when it ships next to the exe, else null. Looked up once per asset.</summary>
    static string? PreviewPath(string asset)
    {
        if (asset.Length == 0) return null;
        if (s_previewPaths.TryGetValue(asset, out var known)) return known;
        string? path = null;
        try
        {
            string candidate = Path.Combine(AppContext.BaseDirectory, "assets", asset.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) path = candidate;
        }
        catch (Exception) { path = null; }
        s_previewPaths[asset] = path;
        return path;
    }

    /// <summary>A window in miniature drawn from the live tokens: a navigation column, a content plate with a title bar in
    /// the accent and two skeleton lines (the board's thumbnail). Proportions are flex ratios, so it fills any card.</summary>
    static Element ThemeMiniature()
    {
        Element Line(float fillGrow, float restGrow, ColorF fill) => new BoxEl
        {
            Direction = 0, Height = Spacing.S, Shrink = 0f, AlignSelf = FlexAlign.Stretch,
            Children = [new BoxEl { Grow = fillGrow, Basis = 0f, Corners = CornerRadius4.All(2f), Fill = fill }, new BoxEl { Grow = restGrow, Basis = 0f }],
        };
        return new BoxEl
        {
            Direction = 0, Height = ClassicSettingsRules.ThemeThumbHeight, AlignSelf = FlexAlign.Stretch, Shrink = 0f,
            Children =
            [
                new BoxEl { Grow = 26f, Basis = 0f, MinWidth = 0f, Fill = Tok.FillLayerAlt },
                new BoxEl
                {
                    Grow = 74f, Basis = 0f, MinWidth = 0f, Direction = 1, Gap = Spacing.S, Fill = Tok.FillCardDefault,
                    Padding = new Edges4(Spacing.M, Spacing.L, Spacing.M, 0f), Corners = new CornerRadius4(6f, 0f, 0f, 0f),
                    Children = [Line(40f, 60f, Tok.AccentDefault), Line(80f, 20f, Tok.TextTertiary with { A = 0.3f }), Line(55f, 45f, Tok.TextTertiary with { A = 0.3f })],
                },
            ],
        };
    }

    // ══ 2. THE ACTIVE THEME'S OPTIONS ═════════════════════════════════════════════════════════════════════════════════

    /// <summary>The options expander (or its quiet one-row stand-in for a theme with nothing to configure).</summary>
    static Element ThemeOptionsExpander() => Embed.Comp(static () => new ThemeOptionsCard());

    /// <summary>The wizard's options: the same rows as plate cards under their group labels, without the actions (a wizard step
    /// resets nothing) and without the legacy rows. Null when the theme offers nothing to pick. Call it from a render: it subscribes
    /// the caller to the option cells, the sidebar and Developer mode, like the expander does.</summary>
    static Element? WizardThemeOptions()
    {
        SettingsOptionsView view = SettingsThemeOptions.Current(wizard: true);
        List<OptionGroup> groups = DrawGroups(view, SettingsFacts.Live, wizard: true);
        if (groups.Count == 0) return null;
        var kids = new List<Element>(groups.Count * 3);
        foreach (OptionGroup group in groups)
        {
            kids.Add(Caption(group.Title) with { Color = Tok.TextSecondary, Weight = 600, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Wrap = TextWrap.NoWrap, MinWidth = 0f, Margin = new Edges4(0f, Spacing.S, 0f, Spacing.XXS) });
            foreach (var (key, row) in group.Rows)
                kids.Add(new BoxEl { Key = key, Direction = 1, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, Children = [row] });
        }
        return new BoxEl
        {
            Key = "settings:theme:wizard-options", Direction = 1, Gap = ClassicSettingsRules.CardSpacing, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
            Layout = MotionRecipes.CardResizeHeight, Children = kids.ToArray(),
        };
    }

    /// <summary>One group of rows under one label, in the order the rows arrived.</summary>
    sealed class OptionGroup(string title)
    {
        public string Title { get; } = title;
        public List<(string Key, Element Row)> Rows { get; } = new(4);
    }

    sealed class ThemeOptionsCard : Component
    {
        public override Element Render()
        {
            var facts = SettingsFacts.Live;
            SettingsOptionsView view = SettingsThemeOptions.Current(wizard: false);
            string title = Strings.Settings.ThemeOptions.Title(Loc.Get(view.NameKey));
            string glyph = SectionGlyph(Tab.Appearance, ThemeOptionsSection.SectionTitle);
            List<OptionGroup> groups = DrawGroups(view, facts, wizard: false);

            // A theme with nothing to configure keeps the row (and its height) and says so; no chevron to nowhere.
            if (groups.Count == 0)
                return Row(title, Loc.Get(Strings.Settings.ThemeOptions.None), null, glyph) with { Key = "settings:theme:options" };

            var items = new List<Element>(groups.Count + 1);
            var titles = new List<string>(groups.Count);
            foreach (OptionGroup group in groups)
            {
                titles.Add(group.Title);
                items.Add(GroupBox(group));
            }
            if (view.Reset is { } reset) items.Add(ResetItem(reset, facts));

            return SettingsExpander.Create(new SettingsExpander.Options
            {
                Header = title,
                Description = string.Join("  ·  ", titles),
                HeaderIcon = glyph,
                Items = items,
                InitiallyExpanded = true,
            }) with { Key = "settings:theme:options" };
        }
    }

    /// <summary>The core's grouped options drawn: a row per entry (a legacy row by its document id, a generated row by its kind, the sidebar's two notes).
    /// A legacy id this page has no control for is left out (and logged once by <see cref="LegacyItem"/>) rather than drawn blank.</summary>
    static List<OptionGroup> DrawGroups(SettingsOptionsView view, ISettingsFacts facts, bool wizard)
    {
        var groups = new List<OptionGroup>(view.Groups.Count);
        foreach (SettingsOptionGroup source in view.Groups)
        {
            var group = new OptionGroup(source.Title);
            foreach (SettingsOptionItem item in source.Items)
            {
                switch (item.Kind)
                {
                    case SettingsOptionItemKind.SidebarFileFault:
                        group.Rows.Add((item.Key, Item(Loc.Get(Strings.Settings.Sidebar.FileFault), "", null, icon: Icons.Warning)));
                        break;
                    case SettingsOptionItemKind.SidebarWhatChanged:
                        group.Rows.Add((item.Key, Item(Loc.Get(Strings.Settings.Sidebar.WhatChanged), item.Text, null, icon: Icons.Info)));
                        break;
                    case SettingsOptionItemKind.Legacy:
                    {
                        SettingsRow row = item.Row!;
                        if (LegacyItem(row.Id, row, facts) is { } legacy) group.Rows.Add((item.Key, legacy));
                        break;
                    }
                    default:
                        group.Rows.Add((item.Key, GeneratedItem(item.Generated!, facts, wizard)));
                        break;
                }
            }
            groups.Add(group);
        }
        return groups;
    }

    /// <summary>A group: its label, then its rows with a hairline between neighbours. The group tweens its own height when a
    /// row comes or goes.</summary>
    static Element GroupBox(OptionGroup group)
    {
        var kids = new List<Element>(group.Rows.Count + 1) { GroupLabel(group.Title) };
        for (int i = 0; i < group.Rows.Count; i++)
            kids.Add(OptionRowBox(group.Rows[i].Key, group.Rows[i].Row, divider: i > 0));
        return new BoxEl
        {
            Key = "opt:group:" + group.Title, Direction = 1, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
            Layout = MotionRecipes.CardResizeHeight, Children = kids.ToArray(),
        };
    }

    static Element OptionRowBox(string key, Element row, bool divider) => new BoxEl
    {
        Key = key, Direction = 1, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
        Children = divider ? [new BoxEl { Height = 1f, AlignSelf = FlexAlign.Stretch, Fill = Tok.StrokeDividerDefault }, row] : [row],
    };

    /// <summary>The group label, aligned with the rows' text column.</summary>
    static Element GroupLabel(string text) => new BoxEl
    {
        AlignSelf = FlexAlign.Stretch, MinWidth = 0f, Padding = new Edges4(ClassicSettingsRules.TextColumnInset, Spacing.M, Spacing.L, Spacing.XS),
        Children = [Caption(text) with { Color = Tok.TextSecondary, Weight = 600, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Wrap = TextWrap.NoWrap, MinWidth = 0f }],
    };

    /// <summary>"Reset options": confirmed like every destructive action (N11), greyed while nothing is stored.</summary>
    static Element ResetItem(SettingsRow reset, ISettingsFacts facts)
    {
        bool enabled = reset.Enabled(facts).Enabled;
        string? sub = reset.DescriptionKey(facts) is { } key ? Loc.Get(key) : null;
        var confirm = reset.Confirm!;
        Element button = Button.Standard(Loc.Get(confirm.PrimaryKey), () =>
            ConfirmThen(Loc.Get(confirm.TitleKey), Loc.Get(confirm.BodyKey), Loc.Get(confirm.PrimaryKey),
                () => { if (reset.Writer.Run!()) Settings.Bump(); }), isEnabled: enabled);
        return OptionRowBox("opt:reset", Item(Loc.Get(reset.TitleKey), sub, button, isEnabled: enabled, icon: Icons.Undo), divider: false);
    }

    // ── a card in the expander (Item) or on the wizard's plate (Row) ────────────────────────────────────────────────────

    static Element OptionCard(bool wizard, string title, string? sub, Element? control, string? icon, bool vertical = false,
                              bool isEnabled = true, bool clickable = false, Action? onClick = null)
    {
        var align = vertical ? SettingsCard.ContentAlignment.Vertical : SettingsCard.ContentAlignment.Right;
        return wizard
            ? Row(title, sub, control, icon, align, clickable, onClick, isEnabled)
            : Item(title, sub, control, align, isEnabled, clickable, onClick, icon);
    }

    // ── the legacy rows (SettingsDoc rows bound to today's keys) ────────────────────────────────────────────────────────

    /// <summary>The control of a row a structure still binds to a legacy key. An id this page has no control for is left out
    /// (and logged) rather than drawn blank.</summary>
    static Element? LegacyItem(string id, SettingsRow row, ISettingsFacts facts)
    {
        string title = Loc.Get(row.TitleKey);
        string? sub = row.DescriptionKey(facts) is { } key ? Loc.Get(key) : null;
        string? icon = row.GlyphRole is { } glyph ? Glyph(glyph) : null;
        bool enabled = row.Enabled(facts).Enabled;
        switch (id)
        {
            case "sidebarReset":
                return Embed.Comp(static () => new SidebarActionItem("sidebarReset", Icons.Undo));
            case "sidebarCustomize":
                return Embed.Comp(static () => new SidebarActionItem("sidebarCustomize", Icons.Edit));
            case "sidebarResetEverything":
                return Embed.Comp(static () => new SidebarActionItem("sidebarResetEverything", Icons.Delete));

            case "trackListStyle":
            {
                int style = row.Current is int current ? current : 0;
                return Item(title, sub, Controls.PickerStrip(2, style, static (i, on) => TrackListStyleCard(i, on), static i =>
                {
                    if (D(Tab.Appearance, "trackListStyle").Writer.Choose!(i)) Settings.Bump();
                }), SettingsCard.ContentAlignment.Vertical, icon: icon);
            }
            case "pageLayout":
            {
                int layout = row.Current is int current ? current : 0;
                return Item(title, sub, Controls.PickerStrip(2, layout, static (i, on) => PageLayoutCard(i, on), static i =>
                {
                    if (D(Tab.Appearance, "pageLayout").Writer.Choose!(i)) Settings.Bump();
                }), SettingsCard.ContentAlignment.Vertical, icon: icon);
            }
            case "railUniform":
                return Item(title, sub, RowToggle(row, enabled), isEnabled: enabled, icon: icon);
            case "railReset":
            {
                var confirm = row.Confirm!;
                return Item(title, sub, Button.Standard(Loc.Get(confirm.PrimaryKey), static () =>
                {
                    var reset = D(Tab.Appearance, "railReset");
                    ConfirmThen(Loc.Get(reset.Confirm!.TitleKey), Loc.Get(reset.Confirm.BodyKey), Loc.Get(reset.Confirm.PrimaryKey),
                        () => { if (reset.Writer.Run!()) Settings.Bump(); });
                }, isEnabled: enabled), icon: icon);
            }
            default:
                Log.Warn("settings", "theme options: no control for the legacy row '" + id + "'; it is left out");
                return null;
        }
    }

    /// <summary>The sidebar layout option (<see cref="ClassicStructure.Sidebar"/>): the preview cards (Classic · Library) under the row's title instead of the
    /// generic text cards. Subscribes to the sidebar's layout signals, so a change made on the pane itself shows here live; while the sidebar is being edited the
    /// cards say why they do not switch.</summary>
    sealed class SidebarLayoutOption(GeneratedOptionRow g, bool wizard) : Component
    {
        public override Element Render()
        {
            var layout = Sidebar.Layout.Value;
            _ = Sidebar.LayoutVersion.Value;
            bool editing = Sidebar.Editing.Value;
            var facts = SettingsFacts.Live;
            return OptionCard(wizard, Loc.Get(g.Row.TitleKey), g.Row.DescriptionKey(facts) is { } key ? Loc.Get(key) : null,
                SidebarLayoutCards(layout, editing), Icons.SplitView, vertical: true);
        }
    }

    /// <summary>The width of an option's combo: the density keeps the narrow combo of the row it replaced.</summary>
    static float ComboWidthOf(GeneratedOptionRow g)
        => g.QualifiedId == ClassicStructure.LibraryDensityOptionId ? ClassicSettingsRules.ZoomComboWidth : ClassicSettingsRules.StageComboWidth;

    /// <summary>Reset layout · Edit sidebar · Reset everything: clickable rows that grey (with the row's reason) while the
    /// sidebar is being edited or has nothing to reset.</summary>
    sealed class SidebarActionItem(string id, string icon) : Component
    {
        public override Element Render()
        {
            _ = Sidebar.Editing.Value;
            _ = Sidebar.LayoutVersion.Value;
            var facts = SettingsFacts.Live;
            var row = D(Tab.Appearance, id);
            return Item(Loc.Get(row.TitleKey), row.DescriptionKey(facts) is { } key ? Loc.Get(key) : null, null,
                isClickEnabled: row.Enabled(facts).Enabled, onClick: () => row.Writer.Run!(), icon: icon);
        }
    }

    // ── the generated rows ───────────────────────────────────────────────────────────────────────────────────────────────

    static readonly Controls.PickerShell OptionCardShell = new(ClassicSettingsRules.OptionCardWidth, ClassicSettingsRules.OptionCardHeight, 8f, 4f);

    /// <summary>One generated row as a card: the control for its kind (themes plan 8.2).</summary>
    static Element GeneratedItem(GeneratedOptionRow g, ISettingsFacts facts, bool wizard)
    {
        SettingsRow row = g.Row;
        string title = Loc.Get(row.TitleKey);
        string? sub = row.DescriptionKey(facts) is { } key ? Loc.Get(key) : null;
        string? icon = row.GlyphRole is { } glyph ? Glyph(glyph) : OptionGlyph(g.QualifiedId);
        bool enabled = row.Enabled(facts).Enabled;

        if (g.Pref is { } pref) return PrefItem(pref, row, title, sub, icon, wizard);
        if (g.QualifiedId == ClassicStructure.SidebarOptionId) return Embed.Comp(() => new SidebarLayoutOption(g, wizard)) with { Key = "opt:sidebar-layout" };

        switch (row.Kind)
        {
            case SettingsRowKind.Toggle:
                return OptionCard(wizard, title, sub,
                    ToggleSwitch.Create(new Signal<bool>(g.Current is true), onChange: on =>
                    {
                        if (row.Writer.Toggle!(on)) Settings.Bump();
                    }, isEnabled: enabled, style: SettingsCard.CompactToggleStyle()), icon, isEnabled: enabled);

            case SettingsRowKind.Segmented:
                return OptionCard(wizard, title, sub,
                    SelectorBar.Create(ChoiceLabels(g), new Signal<int>(g.Current is int at ? at : -1), onChange: i =>
                    {
                        if (row.Writer.Choose!(i)) Settings.Bump();
                    }), icon, isEnabled: enabled);

            case SettingsRowKind.Choice:
                return OptionCard(wizard, title, sub,
                    Embed.Comp(() => new OptionCombo(g)) with { Key = "opt:combo:" + g.QualifiedId + ":" + string.Join('|', g.ChoiceTitleKeys) },
                    icon, isEnabled: enabled);

            case SettingsRowKind.Picker when g.IsColor:
                return OptionCard(wizard, title, sub, OptionColorControl(g), icon, vertical: true, isEnabled: enabled);

            case SettingsRowKind.Picker:
            {
                int selected = g.Current is int chosen ? chosen : -1;
                return OptionCard(wizard, title, sub,
                    Controls.PickerStrip(g.ChoiceTitleKeys.Count, selected, (i, on) => OptionChoiceCard(g, i, on), i =>
                    {
                        if (row.Writer.Choose!(i)) Settings.Bump();
                    }), icon, vertical: true, isEnabled: enabled);
            }

            case SettingsRowKind.Slider:
                return OptionCard(wizard, title, sub, Embed.Comp(() => new OptionSlider(g)) with { Key = "opt:slider:" + g.QualifiedId },
                    icon, isEnabled: enabled);

            case SettingsRowKind.Action:
                return OptionCard(wizard, title, sub, null, icon, isEnabled: enabled, clickable: enabled, onClick: () =>
                {
                    if (row.Writer.Run!()) Settings.Bump();
                });

            default:   // a fact: information only
                return OptionCard(wizard, title, sub, null, icon, isEnabled: enabled);
        }
    }

    /// <summary>The glyph of one of Classic's own options (a generated row carries no catalog glyph): row size for the density, the picture for hide track art,
    /// and the sidebar options kept the icons of the rows they replaced.</summary>
    static string? OptionGlyph(string qualifiedId)
    {
        if (qualifiedId == ClassicStructure.SidebarOptionId) return Icons.SplitView;
        if (qualifiedId == ClassicStructure.LibraryDensityOptionId) return Icons.ViewList;
        if (qualifiedId == ClassicDensity.QualifiedId) return Glyph("RowSize");
        if (qualifiedId == ClassicStructure.HideTrackArtQualifiedId) return Glyph("Picture");
        return null;
    }

    static string[] ChoiceLabels(GeneratedOptionRow g)
    {
        var labels = new string[g.ChoiceTitleKeys.Count];
        for (int i = 0; i < labels.Length; i++) labels[i] = Loc.Get(g.ChoiceTitleKeys[i]);
        return labels;
    }

    /// <summary>A choice as a text card: the theme supplies no preview face for it (that is the `Settings.OptionPreview`
    /// contract of a later PR), so the card carries the choice's name, wrapped to two lines and cut with an ellipsis.</summary>
    static Element OptionChoiceCard(GeneratedOptionRow g, int index, bool on)
    {
        // The density's cards are miniature rows at the real spacing (the picture the old Settings row drew), not a word.
        if (g.QualifiedId == ClassicDensity.QualifiedId) return DensityCard(index, on);
        Element label = Controls.PickerLabel(Loc.Get(g.ChoiceTitleKeys[index]), on) with
        {
            Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.WordEllipsis, MinWidth = 0f, Shrink = 1f,
        };
        return Controls.PickerCard(on, OptionCardShell,
            new BoxEl { Grow = 1f, Direction = 1, Justify = FlexJustify.Center, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = [label] })
            with { Key = "opt:card:" + g.QualifiedId + ":" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) };
    }

    /// <summary>A reference to a core preference: the core row's own control, so there is one writer for it.</summary>
    static Element PrefItem(CorePref pref, SettingsRow row, string title, string? sub, string? icon, bool wizard)
    {
        Element? control = pref switch
        {
            CorePref.AppMode => SelectorBar.Create(ThemeLabels(), new Signal<int>((int)Appearance.Mode), onChange: static m => SetTheme(m)),
            CorePref.Zoom => Embed.Comp(static () => new ZoomPicker()),
            CorePref.Marquee => RowToggle(row),
            CorePref.StageFace => Embed.Comp(static () => new StageVisualizerPicker()),
            _ => null,   // the animation-effects fact: information only
        };
        return OptionCard(wizard, title, sub, control, icon);
    }

    /// <summary>A colour option: its swatches as a strip of circles, or a free picker when it offers none.</summary>
    static Element OptionColorControl(GeneratedOptionRow g)
    {
        if (g.Swatches.Count == 0) return Embed.Comp(() => new OptionColorPicker(g)) with { Key = "opt:color:" + g.QualifiedId };
        int selected = g.Current is int packed ? IndexOfSwatch(g.Swatches, packed) : -1;
        return Controls.PickerStrip(g.Swatches.Count, selected, (i, on) => ColorSwatch(g.Swatches[i], on), i =>
        {
            if ((uint)i < (uint)g.Swatches.Count && g.Row.Writer.Choose!(ThemeOptionsSection.PackColor(g.Swatches[i]))) Settings.Bump();
        });
    }

    static int IndexOfSwatch(IReadOnlyList<ColorF> swatches, int packed)
    {
        for (int i = 0; i < swatches.Count; i++)
            if (ThemeOptionsSection.PackColor(swatches[i]) == packed) return i;
        return -1;
    }

    /// <summary>A 30-DIP circle, not a card; the selection survives a colour-blind read through the heavier ring.</summary>
    static Element ColorSwatch(ColorF color, bool on) => new BoxEl
    {
        Width = ClassicSettingsRules.SwatchSize, Height = ClassicSettingsRules.SwatchSize, Shrink = 0f, Corners = Radii.PillAll, Fill = color,
        BorderWidth = on ? 3f : 1f, BorderColor = on ? Tok.TextPrimary : Tok.StrokeControlDefault,
        HoverScale = R.Motion.ScaleSubtle.Hover, PressScale = R.Motion.ScaleSubtle.Press, Cursor = CursorId.Hand,
    };

    /// <summary>A select drawn as a combo box. The items and signal freeze at mount, so the component keeps the signal in step
    /// with the option's live value; the row is re-keyed (by the caller) when the choices change.</summary>
    sealed class OptionCombo(GeneratedOptionRow g) : Component
    {
        readonly Signal<int> _selected = new(g.Live() is int first ? first : -1);

        public override Element Render()
        {
            _ = ThemeHost.Options.EpochSignal.Value;
            int at = g.Live() is int live ? live : -1;
            UseEffect(() => _selected.SetIfChanged(at), DepKey.From(at));
            return ComboBox.Create(ChoiceLabels(g), _selected, width: ComboWidthOf(g), onChange: i => g.Row.Writer.Choose!(i));
        }
    }

    /// <summary>A slider over the option's range. One signal for the control's life, re-seeded from the live value.</summary>
    sealed class OptionSlider(GeneratedOptionRow g) : Component
    {
        readonly FloatSignal _value = new(g.Live() is float first ? first : g.Min);
        readonly Slider.SliderOptions _options = new()
        {
            Min = g.Min, Max = g.Max, Step = g.Step, TickFrequency = MathF.Max(g.Step, (g.Max - g.Min) / 4f), IsThumbToolTipEnabled = true,
            ThumbToolTipValueConverter = v => SliderText(v, g.UnitKey),
        };

        public override Element Render()
        {
            _ = ThemeHost.Options.EpochSignal.Value;
            float live = g.Live() is float f ? f : g.Min;
            UseEffect(() => _value.SetIfChanged(live), DepKey.From(live));
            return Slider.Create(_value, v => g.Row.Writer.Slide!(v), _options, length: ClassicSettingsRules.SliderLength);
        }
    }

    static string SliderText(float v, string? unitKey)
    {
        string number = v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        return unitKey is { } key ? number + " " + Loc.Get(key) : number;
    }

    /// <summary>A free colour picker. The signal is seeded from the option and written by the picker; every drag step is a
    /// live write (the colour-only retheme path).</summary>
    sealed class OptionColorPicker(GeneratedOptionRow g) : Component
    {
        readonly Signal<ColorF> _color = new(g.Live() is int first ? ThemeOptionsSection.UnpackColor(first) : ClassicSettingsRules.FreePickerSeed);

        public override Element Render()
        {
            _ = ThemeHost.Options.EpochSignal.Value;
            int packed = g.Live() is int live ? live : ThemeOptionsSection.PackColor(_color.Peek());
            UseEffect(() => _color.SetIfChanged(ThemeOptionsSection.UnpackColor(packed)), DepKey.From(packed));
            return ColorPicker.Create(_color, alphaEnabled: false, onChange: c => g.Row.Writer.Choose!(ThemeOptionsSection.PackColor(c)));
        }
    }
}
