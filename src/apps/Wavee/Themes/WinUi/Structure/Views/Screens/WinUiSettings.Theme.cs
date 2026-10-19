// ── Themes/WinUi/Structure/Views/Screens/WinUiSettings.Theme.cs — the theme picker and WinUI's six options (themes plan 6.2, P6 W8b) ──────────────
//
// Boards: W-Settings (-Compact, -Small, -Huge) for the picker, W-ThemeOptions (-Compact, -Small, -Huge) for the options. Settings › Appearance, the theme half: ONE
// SettingsExpander card "Theme" (the current theme's name and a chevron in its header) holding the THEME PICKER (one card per installed theme, driven by
// `ThemePickerFacts`) and, under a "WinUI theme options" label, the ACTIVE THEME'S OPTIONS: exactly the six the theme defines (W-ThemeOptions), in the board's
// order, drawn from `SettingsThemeOptions` (the grouping, the visibility and the Developer gate are the core's; the groups are flattened here because the board
// shows one list). App mode and Zoom follow as their own cards. The setup wizard's Theme step draws the same two views through their own keys
// (`Settings.ThemePicker`, `Settings.ThemeOptions`).
//
// THE SIX, AND HOW EACH APPLIES (technical plan 6.2; the schema declares the mode, this view only writes through the row's writer):
//   Backdrop (Mica, Mica Alt, Acrylic, None)     TRANSACTION: the material is not a colour; the picked theme's busy ring shows while it prewarms
//   Navigation pane (Expanded, Compact, Auto)    LIVE: the pane's width animates over 167 ms; a note says what Automatic does at this window width
//   Density (Default, Compact)                   TRANSACTION: every list re-lays out
//   Tabs in the title bar                        LIVE: the strip's row animates; the workspace is kept
//   Artwork in track rows                        TRANSACTION: row extents
//   Use the Windows accent colour                LIVE: colour only
//
// WHAT THIS FILE NEVER DOES: name a theme. The picker draws `ThemePickerEntry` data; the options draw whatever `SettingsOptionsView` holds (a theme with no options
// gets one quiet row). The board's "Browse community themes" tile is not built: nothing in the core installs a theme from a folder (as in Zune).
//
// NO LAYOUT JUMPS. A picker card has one fixed footprint whatever its text (name: one line, caption: two, ellipsised) and whatever its preview (a miniature when the
// preview asset is missing, the image over it when present); the selected card spends its 1-DIP border growth from its padding, so nothing moves when the selection does.

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

internal static partial class WinUiSettings
{
    // ══ 0. THE KEYED VIEWS ════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.SettingsThemePicker"/>: the picker's cards on a card of their own (the setup wizard's Theme step).</summary>
    public static Element ThemePickerTemplate(SettingsThemePickerProps props, in ViewContext ctx) => Embed.Comp(static () => new ThemePickerCard(chrome: true));

    /// <summary>The registered template of <see cref="ViewKeys.SettingsThemeOptions"/>: the options as cards under their label (the page), or the wizard's plate rows (an
    /// empty element while the theme has nothing to pick there; the wizard step does not place it then).</summary>
    public static Element ThemeOptionsTemplate(SettingsThemeOptionsProps props, in ViewContext ctx)
        => props.Wizard ? WizardThemeOptions() ?? new BoxEl() : Embed.Comp(static () => new OptionsList(wizard: false));

    // ══ 1. THE THEME CARD (the page's expander: picker, then the options) ════════════════════════════════════════════════

    /// <summary>The Theme section's card: the SettingsExpander of W-Settings (palette glyph, "Theme", the current theme's name and a chevron), open, holding the picker
    /// and the options.</summary>
    static Element ThemeSettingsCard() => Embed.Comp(static () => new ThemeExpanderCard());

    sealed class ThemeExpanderCard : SettingsComponent
    {
        public override Element Render()
        {
            var layout = UseLayout();
            var entries = ThemePickerFacts.State.Value.Entries(Platform.Developer.Enabled.Value);
            string current = "";
            foreach (var entry in entries)
                if (entry.Selected) { current = Loc.Get(entry.NameKey); break; }

            var body = new List<Element>(3) { Embed.Comp(static () => new ThemePickerCard(chrome: false)) };
            body.Add(Embed.Comp(static () => new OptionsList(wizard: false)));
            return SettingsExpander.Create(new SettingsExpander.Options
            {
                Header = Loc.Get(Strings.Theme.Winui.Screens.ThemeTitle),
                Description = Loc.Get(Strings.Theme.Winui.Screens.ThemeSub),
                HeaderIcon = Glyph(Catalog.SectionGlyph(Tab.Appearance, "Theme")),
                Content = new TextEl(current) { Size = WinUiSettingsRules.ValueTagSize, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                ItemsHeader = new BoxEl
                {
                    Direction = 1, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, Gap = Spacing.L,
                    Padding = new Edges4(layout.PickerPad + layout.PickerIndent, layout.PickerPad, layout.PickerPad, layout.PickerPad),
                    Children = body.ToArray(),
                },
                InitiallyExpanded = true,
            }) with { Key = "settings:theme:card" };
        }
    }

    // ══ 2. THE THEME PICKER ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The picker's lane (the tier tests find it).</summary>
    internal const string PickerLaneKey = "settings:theme:lane";

    /// <summary>The picker: the radio group of theme cards, in the tier's form (<see cref="WinUiThemePickerForm"/>): four fluid columns (Standard, Huge), one row (Small),
    /// a two-up grid (Compact). ONE TREE at every tier: the group rides the lane box always and the form is the group's wrap and the cards' sizing, so a crossing keeps
    /// the group, its cards and the focused card as the same nodes and the cards glide into their new boxes.</summary>
    sealed class ThemePickerCard(bool chrome) : SettingsComponent
    {
        public override Element Render()
        {
            var layout = UseLayout();
            var viewport = UseContext(Viewport.Size);   // the Compact grid's card width follows the window live inside the tier
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
                // A pick runs a theme transaction: while its prewarm holds the UI the picked card shows a busy ring after 100 ms.
                string? busyId = ThemeHost.SwitchBusy ? ThemeHost.PendingSwitch.Value : null;
                float gridCard = layout.Picker == WinUiThemePickerForm.Grid ? layout.GridCardWidth(viewport.Width) : float.NaN;
                body = new BoxEl
                {
                    Key = PickerLaneKey, Direction = 1, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
                    Children =
                    [
                        Controls.PickerStrip(entries.Count, selected, (i, on) => ThemeCard(entries[i], on, busyId == entries[i].Id, layout, gridCard), PickTheme,
                            parts: PickerParts(layout)),
                    ],
                };
            }
            if (!chrome) return body;

            var card = SettingsCard.DefaultStyle;
            return new BoxEl
            {
                Key = "settings:theme:picker", Direction = 1, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
                Padding = Edges4.All(layout.PickerPad), Corners = CornerRadius4.All(card.CornerRadius), Fill = card.Background,
                BorderWidth = card.BorderWidth, BorderColor = card.Border,
                Children = [body],
            };
        }
    }

    /// <summary>The pick. The picked theme's root mounts fresh (owner Q26: nothing carried), so the Settings page opens again ON Appearance, with the picker as its
    /// first row, instead of on General.</summary>
    static void PickTheme(int index)
    {
        if (SettingsVerbs.ThemeIndex() == index) return;
        Settings.OpenNextMountOn(Settings.Area.Appearance);
        if (D(Tab.Appearance, "themePicker").Writer.Choose!(index)) Settings.Bump();
    }

    /// <summary>The group's grid in the tier's form: the same nodes in every form, only their props differ. The cards are fluid (each column an equal share), the gap
    /// the record's.</summary>
    static TemplateParts PickerParts(WinUiSettingsLayout layout) => layout.Picker == WinUiThemePickerForm.Grid ? s_gridParts : layout.Picker == WinUiThemePickerForm.Row ? s_rowParts : s_cardParts;

    /// <summary>Compact: two columns that wrap (a line drops whole cards).</summary>
    static readonly TemplateParts s_gridParts = new()
    {
        [RadioButtons.PartGrid] = g => g with { Wrap = true, Gap = Spacing.M },
        [RadioButtons.PartColumn] = c => c with { Shrink = 0f },
    };

    /// <summary>Small: one line of equal columns.</summary>
    static readonly TemplateParts s_rowParts = new()
    {
        [RadioButtons.PartGrid] = g => g with { Wrap = false, Gap = Spacing.M, AlignSelf = FlexAlign.Stretch },
        [RadioButtons.PartColumn] = c => c with { Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f },
    };

    /// <summary>Standard and Huge: one line of four equal columns (the board's `repeat(4, 1fr)`): three themes leave the fourth empty, so the cards keep the board's width.</summary>
    static readonly TemplateParts s_cardParts = new()
    {
        [RadioButtons.PartGrid] = g => g with { Wrap = false, Gap = Spacing.M, AlignSelf = FlexAlign.Stretch },
        [RadioButtons.PartColumn] = c => c with { Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, MaxWidth = float.NaN },
    };

    static Element ThemeCard(ThemePickerEntry entry, bool on, bool busy, WinUiSettingsLayout layout, float gridCardWidth)
    {
        string name = Loc.Get(entry.NameKey);
        var nameText = (on ? BodyStrong(name) : BodyStrong(name)) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Wrap = TextWrap.NoWrap, MinWidth = 0f, Shrink = 1f };
        var head = new List<Element>(4) { nameText with { Grow = layout.Picker == WinUiThemePickerForm.Row ? 0f : 1f } };
        if (entry.Availability == ThemeAvailability.Developer && layout.PickerShowsText) head.Add(DeveloperTag());
        if (busy) head.Add(ProgressRing.Indeterminate(size: ProgressRing.MinSize));
        if (on) head.Add(new BoxEl { Shrink = 0f, Children = [Icon(Icons.Accept, WinUiSettingsRules.CheckGlyph, Tok.AccentTextPrimary)] });

        BoxEl nameRow = new BoxEl
        {
            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
            MinHeight = layout.Picker == WinUiThemePickerForm.Grid ? WinUiSettingsTiers.GridNameRowH : 0f,
            Children = head.ToArray(),
        };

        Element[] kids;
        if (layout.Picker == WinUiThemePickerForm.Row)
        {
            // Small: the 48 x 32 thumbnail beside the name, one line.
            kids =
            [
                new BoxEl
                {
                    Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
                    Children = [ThemeThumbnail(entry.Preview, layout, WinUiSettingsRules.RowThumbW) with { Shrink = 0f }, nameRow with { Grow = 1f }],
                },
            ];
        }
        else
        {
            var list = new List<Element>(3) { ThemeThumbnail(entry.Preview, layout, float.NaN), nameRow };
            if (layout.PickerShowsText && entry.DescriptionKey is { } descriptionKey)
                list.Add(Caption(Loc.Get(descriptionKey)) with
                {
                    Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxLines = WinUiSettingsRules.CardDescriptionLines, Trim = TextTrim.WordEllipsis, MinWidth = 0f,
                    AlignSelf = FlexAlign.Stretch,
                });
            kids = list.ToArray();
        }

        // The card: board W-Settings — padding 8, radius 4, subtle fill; the selected one a 2-px accent border over the hover fill. The border grows 1 -> 2 out of
        // the padding (the 1-DIP inset), so the thumbnail never shifts when the selection moves.
        float pad = layout.Picker == WinUiThemePickerForm.Row ? WinUiSettingsRules.RowCardPad : WinUiSettingsRules.CardPad;
        var shell = new BoxEl
        {
            Key = "settings:theme:card:" + entry.Id,
            Width = gridCardWidth, Shrink = float.IsNaN(gridCardWidth) ? 1f : 0f, Direction = 1, Gap = Spacing.S, MinWidth = 0f,
            Padding = Edges4.All(on ? pad - 1f : pad), ClipToBounds = true, Corners = CornerRadius4.All(Radii.Control),
            Fill = on ? Tok.FillSubtleSecondary : Tok.FillCardSecondary, HoverFill = on ? R.Layer.SelectedHover : Tok.FillSubtleSecondary,
            PressedFill = Tok.FillSubtleSecondary,
            BorderWidth = on ? 2f : 1f, BorderColor = on ? Tok.AccentDefault : Tok.StrokeControlDefault,
            HoverScale = R.Motion.ScaleSubtle.Hover, PressScale = R.Motion.ScaleSubtle.Press, Cursor = CursorId.Hand,
            Animate = WinUiScreenMotion.Reflow,   // a crossing re-forms the picker: the card glides into its new box
            Children = kids,
        };
        return float.IsNaN(gridCardWidth) ? shell with { Grow = 1f, Basis = 0f } : shell;
    }

    static Element DeveloperTag() => new BoxEl
    {
        Height = Spacing.L, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f), Corners = Radii.PillAll, Fill = Tok.FillSubtleSecondary,
        Children = [new TextEl(Loc.Get(Strings.Settings.ThemePicker.Developer)) { Size = WinUiSettingsRules.TagSize, Weight = 600, Color = Tok.TextSecondary, MaxLines = 1 }],
    };

    /// <summary>The preview: the theme's preview image over a UI-native miniature (so a missing asset, the state of every theme until it ships one, leaves a thumbnail of
    /// the same size, and the image fades in over it instead of replacing a blank). 16:9 where the width is fluid (Standard, Huge), the record's height at the Compact grid,
    /// the fixed 48 x 32 beside the name at Small.</summary>
    static BoxEl ThemeThumbnail(ThemePreview preview, WinUiSettingsLayout layout, float width)
    {
        bool row = layout.Picker == WinUiThemePickerForm.Row;
        bool grid = layout.Picker == WinUiThemePickerForm.Grid;
        float h = row ? layout.PickerThumbH : grid ? layout.PickerThumbH : float.NaN;
        var layers = new List<Element>(2) { ThemeMiniature(h) };
        string asset = R.IsLight ? preview.LightAsset : preview.DarkAsset;
        if (PreviewPath(asset) is { } path)
            layers.Add(new ImageEl
            {
                Source = path, Fit = ImageFit.Cover, AspectRatio = float.IsNaN(h) ? WinUiSettingsRules.PreviewAspect : float.NaN, Height = h, AlignSelf = FlexAlign.Stretch,
                DecodePx = WinUiSettingsRules.PreviewDecodePx, Placeholder = ColorF.Transparent,
                RevealTransition = ImageTransition.Fade(WinUiSettingsRules.PreviewFadeMs),
            });
        return new BoxEl
        {
            ZStack = true, Width = width, Height = h, AspectRatio = float.IsNaN(h) ? WinUiSettingsRules.PreviewAspect : float.NaN, Shrink = 0f,
            AlignSelf = float.IsNaN(width) ? FlexAlign.Stretch : FlexAlign.Auto, ClipToBounds = true, Corners = CornerRadius4.All(Radii.Control),
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

    /// <summary>A window in miniature drawn from the live tokens, as the board's thumbnail: a title strip (10 %), a navigation column (22 %), a content plate and a
    /// player bar (12 %) with a 2-px accent top edge. Proportions are flex ratios, so it fills any card. NaN <paramref name="height"/>: fills the 16:9 box it sits in.</summary>
    static Element ThemeMiniature(float height)
        => new BoxEl
        {
            Direction = 1, Height = height, Grow = float.IsNaN(height) ? 1f : 0f, AlignSelf = FlexAlign.Stretch, Shrink = 0f, Fill = Tok.FillCardDefault,
            Children =
            [
                new BoxEl { Grow = 10f, Basis = 0f, MinWidth = 0f, Fill = Tok.FillLayerAlt },
                new BoxEl
                {
                    Grow = 78f, Basis = 0f, Direction = 0,
                    Children =
                    [
                        new BoxEl { Grow = 22f, Basis = 0f, MinWidth = 0f, Fill = Tok.FillSolidBaseAlt },
                        new BoxEl { Grow = 78f, Basis = 0f, MinWidth = 0f, Fill = Tok.FillCardDefault, BorderWidth = 0f },
                    ],
                },
                new BoxEl { Grow = 12f, Basis = 0f, MinWidth = 0f, Fill = Tok.FillSolidBase, Direction = 1, Children = [new BoxEl { Height = WinUiSettingsRules.MiniEdge, AlignSelf = FlexAlign.Stretch, Fill = Tok.AccentDefault }] },
            ],
        };

    // ══ 3. THE OPTIONS (exactly the six W-ThemeOptions) ═══════════════════════════════════════════════════════════════

    /// <summary>The wizard's options: the same rows as plate cards under their group labels, without the actions (a wizard step resets nothing) and without the legacy
    /// rows. Null when the theme offers nothing to pick. Call it from a render: it subscribes the caller to the option cells and Developer mode.</summary>
    static Element? WizardThemeOptions()
    {
        SettingsOptionsView view = SettingsThemeOptions.Current(wizard: true);
        List<(string Key, GeneratedOptionRow Row)> rows = OrderedRows(view);
        if (rows.Count == 0) return null;
        var facts = SettingsFacts.Live;
        var kids = new List<Element>(rows.Count + 1)
        {
            Caption(Strings.Theme.Winui.Screens.OptionsTitle(Loc.Get(view.NameKey))) with
            {
                Color = Tok.TextSecondary, Weight = 600, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Wrap = TextWrap.NoWrap, MinWidth = 0f, Margin = new Edges4(0f, Spacing.S, 0f, Spacing.XXS),
            },
        };
        foreach (var (key, row) in rows)
            kids.Add(new BoxEl { Key = key, Direction = 1, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, Children = [OptionCard(row, facts, wizard: true)] });
        return new BoxEl
        {
            Key = "settings:theme:wizard-options", Direction = 1, Gap = WinUiSettingsRules.CardSpacing, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
            Layout = MotionRecipes.CardResizeHeight, Children = kids.ToArray(),
        };
    }

    /// <summary>The generated rows of the view in the board's order (one flat list, not the core's groups: W-ThemeOptions draws one), the rows the board does not name
    /// after it in the core's order. A legacy row is never drawn (WinUI binds none).</summary>
    static List<(string Key, GeneratedOptionRow Row)> OrderedRows(SettingsOptionsView view)
    {
        var all = new List<(string Key, GeneratedOptionRow Row)>(8);
        foreach (SettingsOptionGroup group in view.Groups)
            foreach (SettingsOptionItem item in group.Items)
                if (item.Kind == SettingsOptionItemKind.Generated && item.Generated is { } generated) all.Add((item.Key, generated));
        var ordered = new List<(string Key, GeneratedOptionRow Row)>(all.Count);
        foreach (string id in WinUiScreenRules.OptionOrder)
            for (int i = 0; i < all.Count; i++)
                if (string.Equals(all[i].Row.QualifiedId, id, StringComparison.Ordinal)) { ordered.Add(all[i]); all.RemoveAt(i); break; }
        ordered.AddRange(all);
        return ordered;
    }

    /// <summary>The page's options: the "WinUI theme options" label over one card per option, 4 apart (W-ThemeOptions), then "Reset options" where something is stored.
    /// A theme with nothing to configure keeps one quiet row and says so.</summary>
    sealed class OptionsList(bool wizard) : SettingsComponent
    {
        public override Element Render()
        {
            var layout = UseLayout();
            var tier = Tier;
            var facts = SettingsFacts.Live;
            SettingsOptionsView view = SettingsThemeOptions.Current(wizard);
            string title = Strings.Theme.Winui.Screens.OptionsTitle(Loc.Get(view.NameKey));
            var rows = OrderedRows(view);
            if (rows.Count == 0)
                return Row(title, Loc.Get(Strings.Settings.ThemeOptions.None)) with { Key = "settings:theme:options" };

            var kids = new List<Element>(rows.Count + 2)
            {
                new BoxEl
                {
                    Direction = 1, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, Padding = new Edges4(0f, Spacing.XS, 0f, Spacing.XS),
                    Children = [Caption(title) with { Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Wrap = TextWrap.NoWrap, MinWidth = 0f }],
                },
            };
            foreach (var (key, row) in rows)
            {
                Element card = OptionCard(row, facts, wizard: false);
                string? note = NoteFor(row, tier);
                kids.Add(new BoxEl
                {
                    Key = key, Direction = 1, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, Gap = Spacing.XXS,
                    Children = note is null ? [card] : [card, OptionNote(note)],
                });
            }
            if (view.Reset is { } reset) kids.Add(ResetCard(reset, facts));
            return new BoxEl
            {
                Key = "settings:theme:options", Direction = 1, Gap = WinUiSettingsRules.CardSpacing, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
                Layout = MotionRecipes.CardResizeHeight, Children = kids.ToArray(),
            };
        }
    }

    /// <summary>What an option says under its card (W-ThemeOptions-Compact): the navigation pane's Automatic names the form this window gets and where the others begin.
    /// Null for every other option and while another value is picked (the pick says it all).</summary>
    static string? NoteFor(GeneratedOptionRow g, in TierFacts tier)
    {
        if (!string.Equals(g.QualifiedId, WinUiStructure.NavigationPaneQualifiedId, StringComparison.Ordinal)) return null;
        if (g.Live() is not int live || live != (int)WinUiNavigationPane.Automatic) return null;
        int width = (int)MathF.Round(tier.ViewportWidth);
        if (width <= 0) return null;
        return tier.Window switch
        {
            WidthTier.Compact => Strings.Theme.Winui.Screens.NavNoteMinimal(width, (int)WinUiTiers.SmallFrom, (int)WinUiTiers.StandardFrom),
            WidthTier.Small => Strings.Theme.Winui.Screens.NavNoteRail(width, (int)WinUiTiers.StandardFrom),
            _ => Strings.Theme.Winui.Screens.NavNoteExpanded(width),
        };
    }

    /// <summary>An InfoBar-shaped line under a card: an info glyph and 12-px secondary text.</summary>
    static Element OptionNote(string text) => new BoxEl
    {
        Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Start, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
        Padding = new Edges4(Spacing.L, Spacing.XS, Spacing.L, Spacing.S),
        Children =
        [
            Icon(Icons.Info, WinUiSettingsRules.NoteGlyph, Tok.TextSecondary) with { Shrink = 0f, Margin = new Edges4(0f, 1f, 0f, 0f) },
            Caption(text) with { Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MinWidth = 0f, Grow = 1f, Basis = 0f },
        ],
    };

    /// <summary>"Reset options": confirmed like every destructive action, greyed while nothing is stored.</summary>
    static Element ResetCard(SettingsRow reset, ISettingsFacts facts)
    {
        bool enabled = reset.Enabled(facts).Enabled;
        string? sub = reset.DescriptionKey(facts) is { } key ? Loc.Get(key) : null;
        var confirm = reset.Confirm!;
        Element button = StdButton(Loc.Get(confirm.PrimaryKey), () =>
            ConfirmThen(Loc.Get(confirm.TitleKey), Loc.Get(confirm.BodyKey), Loc.Get(confirm.PrimaryKey),
                () => { if (reset.Writer.Run!()) Settings.Bump(); }), isEnabled: enabled);
        return new BoxEl { Key = "opt:reset", Direction = 1, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, Children = [NestedCard(Loc.Get(reset.TitleKey), sub, button, enabled)] };
    }

    /// <summary>A card nested in the theme card (board: 56 high, 4 radius, the secondary card fill) or on the wizard's plate (the stock card).</summary>
    static Element NestedCard(string title, string? sub, Element? control, bool isEnabled, bool vertical = false, bool clickable = false, Action? onClick = null)
    {
        var align = vertical ? SettingsCard.ContentAlignment.Vertical : SettingsCard.ContentAlignment.Right;
        var style = (InlineStyle(control) ?? SettingsCard.DefaultStyle) with { Background = Tok.FillCardSecondary, MinHeight = WinUiSettingsRules.OptionCardMinHeight };
        return SettingsCard.Create(new SettingsCard.Options
        {
            Header = title, Description = sub, HeaderIcon = null, Content = control, Alignment = Aligned(align, control), Style = style,
            IsClickEnabled = clickable, IsActionIconVisible = clickable, OnClick = onClick, IsEnabled = isEnabled,
        });
    }

    // ── the generated rows ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One generated row as a card: the control for its kind (themes plan 8.2). The six of WinUI are three selects (a ComboBox) and three toggles (a switch
    /// with its On / Off word).</summary>
    static Element OptionCard(GeneratedOptionRow g, ISettingsFacts facts, bool wizard)
    {
        SettingsRow row = g.Row;
        string title = Loc.Get(row.TitleKey);
        string? sub = row.DescriptionKey(facts) is { } key ? Loc.Get(key) : null;
        bool enabled = row.Enabled(facts).Enabled;
        Element Card(Element? control, bool vertical = false, bool clickable = false, Action? onClick = null)
            => wizard ? Row(title, sub, control, null, vertical ? SettingsCard.ContentAlignment.Vertical : SettingsCard.ContentAlignment.Right, clickable, onClick, enabled)
                      : NestedCard(title, sub, control, enabled, vertical, clickable, onClick);

        switch (row.Kind)
        {
            case SettingsRowKind.Toggle:
                return Card(OptionToggle(g, enabled));

            case SettingsRowKind.Segmented:
                return Card(Selector(ChoiceLabels(g), new Signal<int>(g.Current is int at ? at : -1), onChange: i =>
                {
                    if (row.Writer.Choose!(i)) Settings.Bump();
                }));

            case SettingsRowKind.Choice:
                return Card(Embed.Comp(() => new OptionCombo(g)) with { Key = "opt:combo:" + g.QualifiedId + ":" + string.Join('|', g.ChoiceTitleKeys) });

            case SettingsRowKind.Slider:
                return Card(Embed.Comp(() => new OptionSlider(g)) with { Key = "opt:slider:" + g.QualifiedId });

            case SettingsRowKind.Action when row.Confirm is { } confirm:
                return Card(StdButton(Loc.Get(confirm.PrimaryKey), () =>
                    ConfirmThen(Loc.Get(confirm.TitleKey), Loc.Get(confirm.BodyKey), Loc.Get(confirm.PrimaryKey),
                        () => { if (row.Writer.Run!()) Settings.Bump(); }), isEnabled: enabled));

            case SettingsRowKind.Action:
                return Card(null, clickable: enabled, onClick: () =>
                {
                    if (row.Writer.Run!()) Settings.Bump();
                });

            default:   // a fact (or a kind WinUI draws no control for): information only
                return Card(null);
        }
    }

    /// <summary>A switch with its On / Off word (W-ThemeOptions: the state beside the track).</summary>
    static Element OptionToggle(GeneratedOptionRow g, bool enabled)
    {
        var row = g.Row;
        var layout = s_layout;
        var style = ToggleSwitch.DefaultStyle with { MinWidth = layout.ToggleMinWidth, MinHeight = layout.ToggleMinHeight };
        return new BoxEl
        {
            Key = ToggleKey, Direction = 0, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Children =
            [
                ToggleSwitch.Create(new Signal<bool>(g.Current is true), on =>
                {
                    if (row.Writer.Toggle!(on)) Settings.Bump();
                }, onContent: Loc.Get(Strings.Theme.Winui.Screens.On), offContent: Loc.Get(Strings.Theme.Winui.Screens.Off), isEnabled: enabled, style: style),
            ],
        };
    }

    static string[] ChoiceLabels(GeneratedOptionRow g)
    {
        var labels = new string[g.ChoiceTitleKeys.Count];
        for (int i = 0; i < labels.Length; i++) labels[i] = Loc.Get(g.ChoiceTitleKeys[i]);
        return labels;
    }

    /// <summary>A select drawn as a combo box. The items and signal freeze at mount, so the component keeps the signal in step with the option's live value; the row is
    /// re-keyed (by the caller) when the choices change.</summary>
    sealed class OptionCombo(GeneratedOptionRow g) : SettingsComponent
    {
        readonly Signal<int> _selected = new(g.Live() is int first ? first : -1);

        public override Element Render()
        {
            UseLayout();
            _ = ThemeHost.Options.EpochSignal.Value;
            int at = g.Live() is int live ? live : -1;
            UseEffect(() => _selected.SetIfChanged(at), DepKey.From(at));
            return Combo(ChoiceLabels(g), _selected, width: WinUiSettingsRules.OptionComboWidth, onChange: i => g.Row.Writer.Choose!(i));
        }
    }

    /// <summary>A slider over the option's range. One signal for the control's life, re-seeded from the live value.</summary>
    sealed class OptionSlider(GeneratedOptionRow g) : SettingsComponent
    {
        readonly FloatSignal _value = new(g.Live() is float first ? first : g.Min);
        readonly Slider.SliderOptions _options = new()
        {
            Min = g.Min, Max = g.Max, Step = g.Step, TickFrequency = MathF.Max(g.Step, (g.Max - g.Min) / 4f), IsThumbToolTipEnabled = true,
            ThumbToolTipValueConverter = v => SliderText(v, g.UnitKey),
        };

        public override Element Render()
        {
            UseLayout();
            _ = ThemeHost.Options.EpochSignal.Value;
            float live = g.Live() is float f ? f : g.Min;
            UseEffect(() => _value.SetIfChanged(live), DepKey.From(live));
            return SliderControl(_value, v => g.Row.Writer.Slide!(v), _options, length: WinUiSettingsRules.SliderLength);
        }
    }

    static string SliderText(float v, string? unitKey)
    {
        string number = v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        return unitKey is { } key ? number + " " + Loc.Get(key) : number;
    }

    // ══ 4. THE PREVIEW COLUMN (W-Settings-Huge) ═══════════════════════════════════════════════════════════════════════

    /// <summary>The Huge Appearance tab's column beside the cards: "Preview" with the current options as a caption, a miniature of the window, and the navigation pane's
    /// three forms. Drawn from the live tokens; it follows the options as they change.</summary>
    static Element ThemeAside() => Embed.Comp(static () => new ThemeAsideCard());

    sealed class ThemeAsideCard : SettingsComponent
    {
        public override Element Render()
        {
            UseLayout();
            _ = ThemeHost.Options.EpochSignal.Value;
            var view = SettingsThemeOptions.Current(wizard: false);
            var rows = OrderedRows(view);
            var summary = new List<string>(rows.Count);
            foreach (var (_, g) in rows)
            {
                string value = g.Row.Kind == SettingsRowKind.Toggle
                    ? (g.Current is true ? Loc.Get(Strings.Theme.Winui.Screens.On) : Loc.Get(Strings.Theme.Winui.Screens.Off))
                    : g.Live() is int at && at >= 0 && at < g.ChoiceTitleKeys.Count ? Loc.Get(g.ChoiceTitleKeys[at]) : "";
                summary.Add(g.Row.Kind == SettingsRowKind.Toggle ? Loc.Get(g.Row.TitleKey) + " " + value.ToLowerInvariant() : value);
            }
            return new BoxEl
            {
                Key = "settings:aside", Direction = 1, Gap = Spacing.L, Grow = 1f, Basis = 0f, MinWidth = WinUiSettingsRules.AsideMinWidth, AlignSelf = FlexAlign.Start,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 1, Gap = Spacing.XXS, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
                        Children =
                        [
                            BodyStrong(Loc.Get(Strings.Theme.Winui.Screens.PreviewTitle)) with { Size = WinUiSettingsRules.PreviewTitleSize, LineHeight = WinUiSettingsRules.PreviewTitleLine },
                            Caption(string.Join(" · ", summary)) with { Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.WordEllipsis, MinWidth = 0f },
                        ],
                    },
                    WindowMock(),
                ],
            };
        }

        /// <summary>The window in miniature at the aspect of the board's preview: the tab strip over the toolbar, the pane, the content card and the player bar.</summary>
        static Element WindowMock() => new BoxEl
        {
            Key = "settings:aside:window", Direction = 1, AlignSelf = FlexAlign.Stretch, AspectRatio = WinUiSettingsRules.PreviewAspect, MinWidth = 0f,
            ClipToBounds = true, Corners = CornerRadius4.All(Radii.Card), BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, Fill = Tok.FillSolidBase,
            Children =
            [
                new BoxEl { Grow = 6f, Basis = 0f, Fill = Tok.FillSolidBaseAlt },
                new BoxEl { Grow = 7f, Basis = 0f, Fill = Tok.FillSolidBase },
                new BoxEl
                {
                    Grow = 76f, Basis = 0f, Direction = 0,
                    Children =
                    [
                        new BoxEl { Grow = 20f, Basis = 0f, MinWidth = 0f, Fill = Tok.FillSolidBase },
                        new BoxEl { Grow = 80f, Basis = 0f, MinWidth = 0f, Fill = Tok.FillCardDefault, Corners = new CornerRadius4(Radii.Card, 0f, 0f, 0f) },
                    ],
                },
                new BoxEl { Grow = 11f, Basis = 0f, Fill = Tok.FillSolidBase, Direction = 1, Children = [new BoxEl { Height = WinUiSettingsRules.MiniHairline, AlignSelf = FlexAlign.Stretch, Fill = Tok.StrokeDividerDefault }] },
            ],
        };
    }
}
