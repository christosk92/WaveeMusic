// ── Themes/Zune/Structure/Views/Screens/ZuneSettings.Theme.cs — the theme picker, the active theme's options and their preview (themes plan 6.3, Z8b) ─
//
// Boards Z-Settings-Appearance (the picker: a tile 150 x 100 over its name and one line), Z-Theme-Options (the options: label | control rows, the accent as five square
// swatches, the rest as chips and switches, "reset" beside the heading; the preview beside them).
//
// WHAT THIS FILE NEVER DOES: name a theme. The picker draws `ThemePickerEntry` data; the options draw whatever `SettingsOptionsView` holds (a theme with no options gets one
// quiet line). The board's "add a theme from a folder" tile and its community-themes line are not built: nothing in the core installs a theme from a folder.
//
// NO LAYOUT JUMPS. A picker tile has one fixed footprint whatever its text and whatever its preview (a miniature when the preview asset is missing, the image over it when
// present), and a selected tile spends a constant ring on its selection (clear at rest), so the thumbnail never shifts by a pixel. A group around a row that comes or goes
// tweens its height (`ZuneMotion.ColumnCollapse`), so everything below it eases instead of snapping.

using System.Globalization;
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
using Z = Wavee.Themes.Zune.ZuneSettingsRules;

namespace Wavee.Themes.Zune;

internal static partial class ZuneSettings
{
    // ══ the keyed views ══════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.SettingsThemePicker"/>.</summary>
    public static Element ThemePickerTemplate(SettingsThemePickerProps props, in ViewContext ctx)
        => Embed.Comp(static () => new ThemePickerCard()) with { Key = "zs:picker" };

    /// <summary>The registered template of <see cref="ViewKeys.SettingsThemeOptions"/>: the page's heading and rows, or the wizard's rows under their group labels.</summary>
    public static Element ThemeOptionsTemplate(SettingsThemeOptionsProps props, in ViewContext ctx)
        => props.Wizard
            ? Embed.Comp(static () => new ThemeOptionsCard(true)) with { Key = "zs:options:wizard" }
            : Embed.Comp(static () => new ThemeOptionsCard(false)) with { Key = "zs:options" };

    // ══ the picker ═══════════════════════════════════════════════════════════════════════════════════════════════════════

    static readonly Dictionary<ZuneSettingsLayout, TemplateParts> s_pickerParts = new();

    /// <summary>The group's row and columns in the tier's form: fixed tiles that wrap (Standard up), or fluid tiles that wrap two to a line at the least (Compact, Small).</summary>
    static TemplateParts PickerParts(ZuneSettingsLayout lay)
    {
        if (!s_pickerParts.TryGetValue(lay, out var parts))
            s_pickerParts[lay] = parts = new TemplateParts
            {
                [RadioButtons.PartGrid] = g => g with { Wrap = true, Gap = lay.PickerGap },
                [RadioButtons.PartColumn] = c => c with
                {
                    Shrink = 0f, Grow = lay.PickerGrows ? 1f : 0f, Basis = lay.PickerCardW, MinWidth = 0f, MaxWidth = lay.PickerGrows ? Z.PickerFluidMax : lay.PickerCardW,
                },
            };
        return parts;
    }

    /// <summary>The picker: one tile per installed theme, driven by `ThemePickerFacts`; a pick is the `themePicker` row's writer (a theme switch under the page). The picked
    /// theme's root mounts fresh (owner Q26: nothing carried), so the Settings page opens again ON Appearance, with the picker as its first group.</summary>
    sealed class ThemePickerCard : ZuneScreenComponent
    {
        public override Element Render()
        {
            var lay = UseLayout();
            var entries = ThemePickerFacts.State.Value.Entries(Platform.Developer.Enabled.Value);   // tracked: a switch or Developer mode re-renders
            if (entries.Count == 0)
                return ZuneText.Prose(Loc.Get(Strings.Settings.ThemePicker.None)) with { Key = "zs:picker:none" };

            int selected = -1;
            for (int i = 0; i < entries.Count; i++)
                if (entries[i].Selected) { selected = i; break; }
            // A pick runs a theme transaction: while its prewarm holds the UI the picked tile shows a busy ring.
            string? busyId = ThemeHost.SwitchBusy ? ThemeHost.PendingSwitch.Value : null;
            return new BoxEl
            {
                Key = "zs:picker:lane", Direction = 1, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
                Children =
                [
                    Controls.PickerStrip(entries.Count, selected, (i, on) => Tile(entries[i], on, busyId == entries[i].Id, lay), Pick, parts: PickerParts(lay)),
                ],
            };
        }

        static void Pick(int index)
        {
            if (SettingsVerbs.ThemeIndex() == index) return;
            Settings.OpenNextMountOn(Tab.Appearance);
            if (D(Tab.Appearance, "themePicker").Writer.Choose!(index)) Settings.Bump();
        }
    }

    /// <summary>One tile: the thumbnail (with the selection's ring), the name (the accent when picked) and the line under it.</summary>
    static Element Tile(ThemePickerEntry entry, bool on, bool busy, ZuneSettingsLayout lay)
    {
        var name = new List<Element>(3)
        {
            ZuneText.Cased(R.Type.CardTitle(Loc.Get(entry.NameKey)), LabelKind.Label) with
            {
                Color = on ? R.Ink.AccentDecor : Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Wrap = TextWrap.NoWrap, MinWidth = 0f, Shrink = 1f,
            },
        };
        if (entry.Availability == ThemeAvailability.Developer)
            name.Add(ZuneText.Hint(Loc.Get(Strings.Settings.ThemePicker.Developer)) with { Shrink = 0f, Wrap = TextWrap.NoWrap });
        if (busy) name.Add(ProgressRing.Indeterminate(size: ProgressRing.MinSize));
        var kids = new List<Element>(3)
        {
            new BoxEl
            {
                Key = "zs:picker:thumb:" + entry.Id, Direction = 1, AlignSelf = FlexAlign.Stretch, Padding = Edges4.All(Z.SelectGap),
                BorderWidth = Z.SelectRing, BorderColor = on ? Tok.AccentDefault : ColorF.Transparent,
                Children = [Thumbnail(entry.Preview, lay)],
            },
            new BoxEl { Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, Children = name.ToArray() },
        };
        if (lay.PickerShowsText && entry.DescriptionKey is { } descriptionKey)
            kids.Add(ZuneText.Hint(Loc.Get(descriptionKey)) with { MaxLines = Z.PickerTextLines, Trim = TextTrim.WordEllipsis, AlignSelf = FlexAlign.Stretch });
        return new BoxEl
        {
            Key = "zs:picker:card:" + entry.Id, Direction = 1, Gap = Z.PickerCardGap, MinWidth = 0f, ClipToBounds = false, Cursor = CursorId.Hand,
            Animate = ZuneMotion.PivotSlide, Children = kids.ToArray(),
        };
    }

    /// <summary>The preview: the theme's preview image over a UI-native miniature (so a missing asset, the state of every theme until it ships one, leaves a thumbnail of the
    /// same size, and the image fades in over it instead of replacing a blank).</summary>
    static Element Thumbnail(ThemePreview preview, ZuneSettingsLayout lay)
    {
        var layers = new List<Element>(2) { Miniature(lay.PickerThumbH) };
        string asset = R.IsLight ? preview.LightAsset : preview.DarkAsset;
        if (PreviewPath(asset) is { } path)
            layers.Add(new ImageEl
            {
                Source = path, Fit = ImageFit.Cover, Height = lay.PickerThumbH, AlignSelf = FlexAlign.Stretch, DecodePx = Z.PreviewDecodePx,
                Placeholder = ColorF.Transparent, RevealTransition = ImageTransition.Fade(Z.PreviewFadeMs),
            });
        return new BoxEl { ZStack = true, Height = lay.PickerThumbH, Shrink = 0f, AlignSelf = FlexAlign.Stretch, ClipToBounds = true, Fill = Tok.FillSolidBase, Children = layers.ToArray() };
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

    /// <summary>A window in miniature drawn from the live tokens: a navigation strip, a content plate with a title line in the accent and two skeleton lines, the bar under
    /// it. Proportions are flex ratios, so it fills any tile.</summary>
    static Element Miniature(float height)
    {
        Element Line(float fillGrow, float restGrow, ColorF fill) => new BoxEl
        {
            Direction = 0, Height = Spacing.S, Shrink = 0f, AlignSelf = FlexAlign.Stretch,
            Children = [new BoxEl { Grow = fillGrow, Basis = 0f, Fill = fill }, new BoxEl { Grow = restGrow, Basis = 0f }],
        };
        return new BoxEl
        {
            Direction = 1, Height = height, AlignSelf = FlexAlign.Stretch, Shrink = 0f, Fill = R.Layer.ShellGround,
            Children =
            [
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, MinHeight = 0f, Gap = Spacing.S, Padding = new Edges4(Spacing.M, Spacing.M, Spacing.M, 0f),
                    Children =
                    [
                        Line(Z.MiniTitleFill, Z.MiniTitleRest, R.Ink.AccentDecor),
                        Line(Z.MiniLineFill, Z.MiniLineRest, Tok.TextTertiary with { A = Z.SkeletonAlpha }),
                        Line(Z.MiniLine2Fill, Z.MiniLine2Rest, Tok.TextTertiary with { A = Z.SkeletonAlpha }),
                    ],
                },
                new BoxEl { Height = Z.MiniBarHeight, AlignSelf = FlexAlign.Stretch, Shrink = 0f, Fill = R.Layer.PlayerBar },
            ],
        };
    }

    // ══ the options ══════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The active theme's options: the heading ("‹name› options") with "reset" beside it, then the groups' rows. The wizard's variant has no reset and no actions, its
    /// rows always stack (the plate is narrow), and a theme with nothing to pick there draws an empty element (the wizard step does not place it then).</summary>
    sealed class ThemeOptionsCard(bool wizard) : ZuneScreenComponent
    {
        public override Element Render()
        {
            var lay = UseLayout();
            var k = new ZuneKit(wizard ? lay with { StacksRows = true } : lay);
            var facts = SettingsFacts.Live;
            SettingsOptionsView view = SettingsThemeOptions.Current(wizard);
            string title = Strings.Settings.ThemeOptions.Title(Loc.Get(view.NameKey));

            var kids = new List<Element>(8);
            var head = new List<Element>(2) { ZuneText.Heading(title) with { Shrink = 0f, Wrap = TextWrap.NoWrap } };
            if (!wizard && view.Reset is { } reset)
            {
                var confirm = reset.Confirm!;
                head.Add(k.Cmd(Loc.Get(Strings.Settings.ThemeOptions.Reset), () => ConfirmThen(Loc.Get(confirm.TitleKey), Loc.Get(confirm.BodyKey), Loc.Get(confirm.PrimaryKey),
                    () => { if (reset.Writer.Run!()) Settings.Bump(); }), reset.Enabled(facts).Enabled));
            }
            kids.Add(new BoxEl
            {
                Key = "zs:options:head", Direction = 0, Wrap = true, AlignItems = FlexAlign.End, Gap = Z.HeadGap, MinWidth = 0f, Children = head.ToArray(),
            });

            int rows = 0;
            foreach (SettingsOptionGroup group in view.Groups)
            {
                var items = new List<Element>(group.Items.Count);
                foreach (SettingsOptionItem item in group.Items)
                    if (item.Kind == SettingsOptionItemKind.Generated && Draw(k, item.Generated!, facts) is { } row)
                        items.Add(row with { Key = item.Key });
                if (items.Count == 0) continue;
                rows += items.Count;
                kids.Add(new BoxEl
                {
                    Key = "zs:opt:group:" + group.Title, Direction = 1, Gap = Z.RowGap, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, Animate = ZuneMotion.ColumnCollapse,
                    Children = [ZuneText.Cased(R.Type.Eyebrow(group.Title), LabelKind.Heading) with { Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis }, .. items],
                });
            }
            // A theme with nothing to configure keeps the heading and says so; the wizard places nothing.
            if (rows == 0)
            {
                if (wizard) return new BoxEl { Key = "zs:options:none" };
                kids.Add(ZuneText.Prose(Loc.Get(Strings.Settings.ThemeOptions.None)));
            }
            else if (!wizard)
                kids.Add(ZuneText.Hint(Loc.Get(Strings.Settings.ThemeOptions.ResetSub)));

            return new BoxEl
            {
                Key = wizard ? "zs:options:wizard-rows" : "zs:options:rows", Direction = 1, Gap = Z.RowGap, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
                Animate = ZuneMotion.ColumnCollapse, Children = kids.ToArray(),
            };
        }
    }

    /// <summary>One generated row as a Zune row: the control for its kind. A kind this page has no control for (a legacy action, an unknown) is left out.</summary>
    static Element? Draw(ZuneKit k, GeneratedOptionRow g, ISettingsFacts facts)
    {
        SettingsRow row = g.Row;
        string title = Loc.Get(row.TitleKey);
        string? sub = row.DescriptionKey(facts) is { } key ? Loc.Get(key) : null;
        bool enabled = row.Enabled(facts).Enabled && g.Enabled;
        string rowKey = "zs:opt:" + g.QualifiedId;

        if (g.Pref is { } pref)
        {
            Element? control = pref switch
            {
                CorePref.AppMode => k.Choice(row, ModeLabels(), chips: true),
                CorePref.Zoom => Embed.Comp(static () => new ZoomCell()) with { Key = "zs:zoom" },
                CorePref.Marquee => k.Toggle(row),
                CorePref.StageFace => k.Choice(row, VisualizerLabels(), chips: false, width: Z.WideComboWidth),
                _ => null,   // the animation-effects fact: information only
            };
            return k.Row(rowKey, title, sub, control);
        }

        switch (row.Kind)
        {
            case SettingsRowKind.Toggle:
                return k.Row(rowKey, title, sub, k.Switch(g.Current is true, on => { if (row.Writer.Toggle!(on)) Settings.Bump(); }, enabled), enabled);

            case SettingsRowKind.Segmented:
            case SettingsRowKind.Choice:
            case SettingsRowKind.Picker when !g.IsColor:
            {
                string[] labels = ChoiceLabels(g);
                bool chips = labels.Length <= Z.MaxChips && g.Picker != PickerStyle.Combo;
                return k.Row(rowKey, title, sub, k.Choice(g.QualifiedId, new ZuneChoice(labels, () => g.Live() is int at ? at : -1,
                    i => { if (row.Writer.Choose!(i)) Settings.Bump(); }, chips, enabled ? null : static () => false, Width: Z.ComboWidth)), enabled);
            }

            case SettingsRowKind.Picker:
                return k.Row(rowKey, title, sub, Embed.Comp(() => new ColorOption(g)) with { Key = "zs:opt:color:" + g.QualifiedId }, enabled);

            case SettingsRowKind.Slider:
                return k.Row(rowKey, title, sub, k.Slide(g.QualifiedId, new ZuneSlide(g.Min, g.Max, g.Step,
                    () => g.Live() is float f ? f : g.Min, v => row.Writer.Slide!(v), v => SliderText(v, g.UnitKey), enabled ? null : static () => false)), enabled);

            case SettingsRowKind.Action when row.Confirm is { } confirm:
                // A destructive action: a command that asks first.
                return k.Row(rowKey, title, sub, k.Cmd(Loc.Get(confirm.PrimaryKey), () => ConfirmThen(Loc.Get(confirm.TitleKey), Loc.Get(confirm.BodyKey),
                    Loc.Get(confirm.PrimaryKey), () => { if (row.Writer.Run!()) Settings.Bump(); }), enabled), enabled);

            case SettingsRowKind.Action:
                return k.Row(rowKey, title, sub, k.Cmd(title, () => { if (row.Writer.Run!()) Settings.Bump(); }, enabled), enabled);

            default:   // a fact: information only
                return k.Row(rowKey, title, sub, null, enabled);
        }
    }

    static string[] ChoiceLabels(GeneratedOptionRow g)
    {
        var labels = new string[g.ChoiceTitleKeys.Count];
        for (int i = 0; i < labels.Length; i++) labels[i] = Loc.Get(g.ChoiceTitleKeys[i]);
        return labels;
    }

    static string SliderText(float v, string? unitKey)
    {
        string number = v.ToString("0.##", CultureInfo.InvariantCulture);
        return unitKey is { } key ? number + " " + Loc.Get(key) : number;
    }

    /// <summary>A colour option: its swatches as a row of squares (the picked one ringed), or a free picker when it offers none.</summary>
    sealed class ColorOption(GeneratedOptionRow g) : ZuneScreenComponent
    {
        readonly Signal<ColorF> _color = new(g.Live() is int first ? ThemeOptionsSection.UnpackColor(first) : Z.FreePickerSeed);

        public override Element Render()
        {
            var lay = UseLayout();
            _ = ThemeHost.Options.EpochSignal.Value;
            int packed = g.Live() is int live ? live : ThemeOptionsSection.PackColor(_color.Peek());
            UseEffect(() => _color.SetIfChanged(ThemeOptionsSection.UnpackColor(packed)), DepKey.From(packed));
            if (g.Swatches.Count == 0)
            {
                return ColorPicker.Create(_color, alphaEnabled: false, onChange: c => g.Row.Writer.Choose!(ThemeOptionsSection.PackColor(c)));
            }
            int selected = -1;
            for (int i = 0; i < g.Swatches.Count; i++)
                if (ThemeOptionsSection.PackColor(g.Swatches[i]) == packed) { selected = i; break; }
            float edge = MathF.Max(lay.SwatchSize, lay.Target);
            return Controls.PickerStrip(g.Swatches.Count, selected, (i, on) => new BoxEl
            {
                Key = "zs:swatch:" + i.ToString(CultureInfo.InvariantCulture), Width = edge, Height = edge, Shrink = 0f, Fill = g.Swatches[i],
                BorderWidth = Z.SelectRing, BorderColor = on ? Tok.TextPrimary : ColorF.Transparent, Margin = Edges4.All(Z.SelectGap),
                Cursor = CursorId.Hand,
            }, i =>
            {
                if ((uint)i < (uint)g.Swatches.Count && g.Row.Writer.Choose!(ThemeOptionsSection.PackColor(g.Swatches[i]))) Settings.Bump();
            });
        }
    }

    // ══ the preview ══════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The preview beside the options (Appearance, Standard up): a page in miniature drawn from the live roles, so a pick of the accent or the numerals shows in it at
    /// once. It sits beside the column where there is room and wraps under it where there is not.</summary>
    static Element Aside(ZuneSettingsLayout lay)
    {
        string[] pivots =
        [
            Loc.Get(Strings.Theme.Zune.Screens.PreviewPivotHome), Loc.Get(Strings.Theme.Zune.Screens.PreviewPivotBrowse),
            Loc.Get(Strings.Theme.Zune.Screens.PreviewPivotLibrary), Loc.Get(Strings.Theme.Zune.Screens.PreviewPivotRecents),
        ];
        var nav = new Element[pivots.Length];
        for (int i = 0; i < nav.Length; i++)
            nav[i] = ZuneText.Cased(R.Type.SubPivot(pivots[i]), LabelKind.Navigation) with { Color = i == Z.PreviewOpenPivot ? Tok.TextPrimary : Tok.TextTertiary, Shrink = 0f, MaxLines = 1 };

        string[] tracks =
        [
            Loc.Get(Strings.Theme.Zune.Screens.PreviewTrack1), Loc.Get(Strings.Theme.Zune.Screens.PreviewTrack2),
            Loc.Get(Strings.Theme.Zune.Screens.PreviewTrack3), Loc.Get(Strings.Theme.Zune.Screens.PreviewTrack4),
        ];
        var rows = new Element[tracks.Length];
        for (int i = 0; i < rows.Length; i++)
        {
            bool playing = i == Z.PreviewPlayingRow;
            ColorF ink = playing ? R.Ink.AccentDecor : Tok.TextPrimary;
            rows[i] = new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinWidth = 0f, Height = Z.PreviewRowHeight,
                Children =
                [
                    new BoxEl
                    {
                        Width = Z.PreviewNumberWidth, Shrink = 0f,
                        Children = [R.Type.Numeral((i + 1).ToString(CultureInfo.InvariantCulture)) with { Color = playing ? R.Ink.AccentDecor : Tok.TextTertiary, MaxLines = 1 }],
                    },
                    R.Type.DenseMeta(tracks[i]) with { Color = ink, Grow = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                ],
            };
        }

        Element page = new BoxEl
        {
            Key = "zs:preview:page", Direction = 1, Gap = Spacing.S, Grow = 1f, MinHeight = 0f, Padding = Edges4.All(Spacing.L), ClipToBounds = true,
            Fill = R.Layer.ShellGround,
            Children =
            [
                new BoxEl { Direction = 0, Gap = Spacing.M, MinWidth = 0f, Children = nav },
                ZuneText.Cased(R.Type.PageHero(Loc.Get(Strings.Theme.Zune.Screens.PreviewTitle)), LabelKind.Heading) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                new BoxEl { Direction = 1, MinWidth = 0f, Children = rows },
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S,
                    Children =
                    [
                        Disc(Icons.Play) with { Width = Z.PreviewDisc, Height = Z.PreviewDisc },
                        ZuneText.Command(Loc.Get(Strings.Theme.Zune.Screens.PreviewPlay)),
                    ],
                },
            ],
        };
        Element bar = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Height = Z.PreviewBarHeight, Shrink = 0f, Padding = new Edges4(Spacing.L, 0f, Spacing.L, 0f), Fill = R.Layer.PlayerBar,
            Children = [R.Type.MicroMeta(Loc.Get(Strings.Theme.Zune.Screens.PreviewNowPlaying)) with { Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f }],
        };
        return new BoxEl
        {
            Key = "zs:aside", Direction = 1, Gap = Spacing.S, Basis = Z.AsideBasis, Grow = 1f, Shrink = 1f, MinWidth = 0f, MaxWidth = Z.AsideMax,
            Animate = ZuneMotion.PivotSlide,
            Children =
            [
                ZuneText.Hint(Loc.Get(Strings.Theme.Zune.Screens.PreviewLabel)),
                new BoxEl
                {
                    Direction = 1, Height = Z.PreviewHeight, ClipToBounds = true, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, Fill = R.Layer.ShellGround,
                    Children = [page, bar],
                },
                ZuneText.Hint(Loc.Get(Strings.Theme.Zune.Screens.PreviewNote)),
            ],
        };
    }

    static BoxEl Disc(string glyph) => ZuneKit.Disc(glyph);
}
