// ── Themes/Classic/Structure/Views/Screens/ClassicSettings.Appearance.cs — the Appearance tab (themes plan 8.1, T1b; T11b) ───────────────────
//
// From `Screens\Settings.UI.Appearance.cs`. Theme (the picker, `ClassicSettings.Theme.cs`) · the active theme's options (one expander, its rows grouped) ·
// App mode · Zoom · Marquee, then what is not a theme option and has no other home yet: Lyrics (blur, the on-device AI lyrics card) · Fullscreen (visualizer,
// layout, sensitivity, lyrics overlay, sync offset, calm motion, change with the music — the fullscreen stage's gallery settings, mirrored through the
// document) · Now playing (hero*, player style) · Home (the daylist clock). The sidebar, track-list and surface cards (the picture of the legacy rows a structure
// still binds) are in this file's §4-§5.
//
// * DEVELOPER-ONLY rows (`Catalog.RowVisible`, the document's `Visible`): the Cover/‹Player› hero switch is composed away while the
// developer switch is off. "Lyrics second line" and "Animated lyrics backdrop" are not gated but GONE — neither is a setting any more.
//
// EVERY ROW WRITES THROUGH ITS DOCUMENT ROW'S WRITER (which bumps the epoch it always bumped: ch 27 W2 "three facts"). The tab body reads the document, never a
// foreign epoch it does not need, so a write re-renders this page once, through `Settings.Bump()`.
//
// FROZEN CONTROL INPUTS. A ComboBox freezes its items, its signal AND its onChange at mount: the zoom and player-style combos are therefore tiny child
// components that keep one stable signal in step through an effect, and every handler reads the store at invocation (through the row), never a render local.
// SelectorBar / Slider / CheckBox re-push their props.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;
using Tab = Wavee.Settings.Tab;

namespace Wavee.Themes.Classic;

internal static partial class ClassicSettings
{
    // ══ 1. STABLE CONTROL STATE (0.2.9's `_lyricsBlurSlider` instance field + the combo selections) ═════════════════

    /// <summary>The lyrics-blur slider's value (the resolved 0..100). A slider binds ONE signal for its life.</summary>
    static readonly FloatSignal s_lyricsBlurSlider = new(Platform.Keys.LyricsBlurStrength.Default);
    /// <summary>The zoom combo's selection, kept in step with the LIVE zoom by <see cref="ZoomPicker"/>.</summary>
    static readonly Signal<int> s_zoomIndex = new(0);
    /// <summary>The daylist-clock combo's selection (the <see cref="DaylistClockMode"/> int), kept in step with the store by
    /// <see cref="DaylistClockPicker"/>.</summary>
    static readonly Signal<int> s_daylistClock = new((int)DaylistClockMode.Both);
    /// <summary>The player-style combo's selection, kept in step with every other style writer by <see cref="NpvStylePicker"/>.</summary>
    static readonly Signal<int> s_npvStyle = new(Rail.PlayerCatalog.DefaultPresetId);

    static readonly Slider.SliderOptions s_lyricsBlurOptions = new()
    {
        Min = 0f, Max = 100f, Step = 1f, TickFrequency = 25f, IsThumbToolTipEnabled = true,
        ThumbToolTipValueConverter = static v => ((int)MathF.Round(v)).ToString(System.Globalization.CultureInfo.InvariantCulture) + "%",
    };

    // The fullscreen stage's three value controls (a ComboBox and two sliders). Each is bound for the control's life, so a
    // child component re-seeds it from the document on every epoch (StageVisualizerPicker / StageSensitivitySlider /
    // StageSyncSlider below) and a pick made in the stage's gallery shows here live. The combo's value is an INDEX into
    // Visualizer.Catalog.Shown (the gallery's order), never the persisted Kind int.
    static readonly Signal<int> s_stageVisualizer = new(Visualizer.Catalog.IndexOf(Visualizer.Catalog.Default));
    /// <summary>The stage's visualizer layout (Card · Large art · Centered · Artist) as the combo's index — the persisted int itself.</summary>
    static readonly Signal<int> s_stageLayout = new(0);
    static readonly FloatSignal s_stageSensitivity = new(1f), s_stageSyncOffset = new(0f);
    static readonly Slider.SliderOptions s_stageSensitivityOptions = new()
    {
        Min = 0.3f, Max = 1.5f, Step = 0.05f, TickFrequency = 0.3f, IsThumbToolTipEnabled = true,
        ThumbToolTipValueConverter = static v => ((int)MathF.Round(v * 100f)).ToString(System.Globalization.CultureInfo.InvariantCulture) + "%",
    };
    static readonly Slider.SliderOptions s_stageSyncOptions = new()
    {
        Min = -500f, Max = 500f, Step = 10f, TickFrequency = 250f, IsThumbToolTipEnabled = true,
        ThumbToolTipValueConverter = static v => ((int)MathF.Round(v)).ToString(System.Globalization.CultureInfo.InvariantCulture) + " ms",
    };

    /// <summary>The ladder rungs as labels, hoisted once — digits and '%' never change with culture.</summary>
    static readonly string[] s_zoomRungLabels = BuildZoomRungLabels();

    /// <summary>The page mount: every frozen control's signal from the document's value (`row.Current`).</summary>
    static void SeedAppearance()
    {
        s_lyricsBlurSlider.Value = D(Tab.Appearance, "lyricsBlur").Current is float blur ? blur : s_lyricsBlurSlider.Value;
        s_stageVisualizer.Value = D(Tab.Appearance, "stageVisualizer").Current is int visualizer ? visualizer : s_stageVisualizer.Value;
        s_stageLayout.Value = D(Tab.Appearance, "stageLayout").Current is int layout ? layout : s_stageLayout.Value;
        s_stageSensitivity.Value = D(Tab.Appearance, "stageSensitivity").Current is float sensitivity ? sensitivity : s_stageSensitivity.Value;
        s_stageSyncOffset.Value = D(Tab.Appearance, "stageSyncOffset").Current is float offset ? offset : s_stageSyncOffset.Value;
    }

    // ══ 2. THE TAB ════════════════════════════════════════════════════════════════════════════════════════════════════

    private static Element AppearanceTab()
    {
        // The now-playing rows' ONE foreign epoch (ch 27 §7): the header row, the flyout, the art menu and the palette all
        // write it, and these two rows must follow them live.
        int npvPresentation = Rail.PlayerPrefs.Presentation();
        var npvPreset = Rail.PlayerPrefs.CurrentPreset();
        var facts = SettingsFacts.Live;   // developer mode is read reactively here, as the tab always did

        // T1b (themes plan 8.1): Theme (the picker) · the active theme's options · App mode · Zoom, then what is not a theme
        // option and has no other home yet. The picker and the options are keyed views that re-render on their own signals.
        var kids = new List<Element>(24)
        {
            SectionHeader(Loc.Get(Strings.Settings.Appearance.Title), SectionGlyph(Tab.Appearance, "Theme"),
                Loc.Get(Strings.Settings.ThemePicker.Subtitle)),
            ThemePickerView(),
            ThemeOptionsExpander(),
            // The core presentation prefs every theme honours: no header, so they read as the board's rows under the options.
            DocRow(D(Tab.Appearance, "appMode"),
                SelectorBar.Create(ThemeLabels(), new Signal<int>((int)Appearance.Mode), onChange: static m => SetTheme(m))),
            // A ComboBox, not a SelectorBar: thirteen items (Auto + twelve rungs) would be thirteen segments wide.
            DocRow(D(Tab.Appearance, "zoom"), Embed.Comp(static () => new ZoomPicker())),
            DocRow(D(Tab.Appearance, "marquee"), RowToggle(D(Tab.Appearance, "marquee"))),
        };

        // The blur dial, then the on-device AI lyrics card (its own component: setup progress re-renders only the card).
        // The second line and the animated backdrop are no longer settings at all.
        kids.Add(SectionHeader(Loc.Get(Strings.Settings.Lyrics.Title), SectionGlyph(Tab.Appearance, "Lyrics"),
            Loc.Get(Strings.Settings.Lyrics.Subtitle)));
        kids.Add(DocRow(D(Tab.Appearance, "lyricsBlur"), LyricsBlurControl()));
        kids.Add(AiLyricsCard());

        // ── the fullscreen stage: the gallery's four settings, mirrored here with the rest of Appearance ──
        kids.Add(SectionHeader(Loc.Get(Strings.Stage.Settings.Title), SectionGlyph(Tab.Appearance, "Fullscreen"), Loc.Get(Strings.Stage.Settings.Subtitle)));
        kids.Add(DocRow(D(Tab.Appearance, "stageVisualizer"), Embed.Comp(static () => new StageVisualizerPicker())));
        kids.Add(DocRow(D(Tab.Appearance, "stageLayout"), Embed.Comp(static () => new StageLayoutPicker())));
        kids.Add(DocRow(D(Tab.Appearance, "stageSensitivity"), Embed.Comp(static () => new StageSensitivitySlider())));
        kids.Add(DocRow(D(Tab.Appearance, "stageLyricsOverlay"), RowToggle(D(Tab.Appearance, "stageLyricsOverlay"))));
        kids.Add(DocRow(D(Tab.Appearance, "stageSyncOffset"), Embed.Comp(static () => new StageSyncSlider())));
        kids.Add(DocRow(D(Tab.Appearance, "stageCalm"), RowToggle(D(Tab.Appearance, "stageCalm"))));
        kids.Add(DocRow(D(Tab.Appearance, "stageMoments"), RowToggle(D(Tab.Appearance, "stageMoments"))));

        kids.Add(SectionHeader(Loc.Get(Strings.Settings.NowPlaying.Title), SectionGlyph(Tab.Appearance, "Now playing"),
            Loc.Get(Strings.Settings.NowPlaying.Subtitle)));
        // The second label FOLLOWS the current style; SelectorBar re-pushes its items, so it relabels in place.
        // DEVELOPER-ONLY, exactly like the rail header switch it duplicates — a normal build shows the cover.
        if (D(Tab.Appearance, "npvPresentation").Visible(facts))
            kids.Add(DocRow(D(Tab.Appearance, "npvPresentation"),
                SelectorBar.Create([Loc.Get(Strings.Player.PresentationCover), Loc.Get(npvPreset.ShortLabelKey)],
                    new Signal<int>(npvPresentation),
                    onChange: static i => D(Tab.Appearance, "npvPresentation").Writer.Choose!(i))));
        kids.Add(DocRow(D(Tab.Appearance, "npvStyle"), Embed.Comp(static () => new NpvStylePicker())));

        kids.Add(SectionHeader(Loc.Get(Strings.Settings.Home.Title), SectionGlyph(Tab.Appearance, "Home"),
            Loc.Get(Strings.Settings.Home.Subtitle)));
        // A ComboBox in its own component, the zoom/player-style shape: the items, signal and onChange freeze at mount.
        kids.Add(DocRow(D(Tab.Appearance, "daylistClock"), Embed.Comp(static () => new DaylistClockPicker())));

        return TabStack(kids.ToArray());
    }

    // ══ 3. THEME + ZOOM ═══════════════════════════════════════════════════════════════════════════════════════════════

    static string[] ThemeLabels() =>
    [
        Loc.Get(Strings.Settings.Choice.System),
        Loc.Get(Strings.Settings.Choice.Light),
        Loc.Get(Strings.Settings.Choice.Dark),
    ];

    /// <summary>0 System · 1 Light · 2 Dark: the row's writer swaps the installed Wavee palette IN PLACE (System re-reads the OS theme and
    /// accent now), persists, and asks the host for the 250 ms re-theme cross-fade (ch 27 §5); this page then re-reads its rows.</summary>
    static void SetTheme(int mode)
    {
        if (D(Tab.Appearance, "appMode").Writer.Choose!(mode)) Settings.Bump();
    }

    static string[] BuildZoomRungLabels()
    {
        var labels = new string[ZoomLadder.Steps.Length];
        for (int i = 0; i < labels.Length; i++)
            labels[i] = ZoomLadder.Percent(ZoomLadder.Steps[i]).ToString(System.Globalization.CultureInfo.InvariantCulture) + "%";
        return labels;
    }

    /// <summary>The Zoom row's combo. Index 0 is "Auto (N%)" with the LIVE resolved percent and is the selection for BOTH
    /// Auto and Dense; only Manual selects a rung (ch 27 W2). Its own component because it needs the live zoom, the
    /// viewport and one effect: a chord or the Ctrl+wheel moves the selection with no settings write.</summary>
    sealed class ZoomPicker : Component
    {
        float _auto = 1f;
        int _labelsPercent = -1;
        string[] _labels = [];

        public override Element Render()
        {
            float zoom = UseContext(Viewport.Zoom);
            var viewport = UseContext(Viewport.Size);
            // baseDip = the live (already zoomed) viewport × zoom — ZoomAutoPolicy takes BASE dips, never live ones.
            _auto = ZoomService.AutoSuggestion(viewport.Width, viewport.Height, zoom);
            SettingsFacts.PublishAutoZoom(_auto);   // what the head item ("Auto (N%)") applies when picked
            var mode = ZoomService.Mode;
            int index = mode == ZoomAutoMode.Manual
                ? 1 + Math.Max(0, Array.IndexOf(ZoomLadder.Steps, ZoomLadder.Snap(zoom)))
                : 0;
            UseEffect(() => s_zoomIndex.Value = index, DepKey.From(index));

            int percent = ZoomLadder.Percent(_auto);
            if (percent != _labelsPercent)
            {
                _labelsPercent = percent;
                _labels = new string[s_zoomRungLabels.Length + 1];
                _labels[0] = Strings.Settings.Appearance.ZoomAuto(percent);
                Array.Copy(s_zoomRungLabels, 0, _labels, 1, s_zoomRungLabels.Length);
            }
            // The combo's ITEMS freeze at mount: the head label changes only on a plateau change, so re-key on that.
            return ComboBox.Create(_labels, s_zoomIndex, width: ClassicSettingsRules.ZoomComboWidth, onChange: Pick) with { Key = "appearance.zoom:" + percent };
        }

        /// <summary>A deliberate pick writes at once (the chords' debounce exists for key repeats). The head item writes
        /// mode = Auto — never Dense — and applies THIS render's suggestion; a rung writes Manual.</summary>
        void Pick(int i)
        {
            if (D(Tab.Appearance, "zoom").Writer.Choose!(i)) Settings.Bump();
        }
    }

    // ══ 4. LISTS ══════════════════════════════════════════════════════════════════════════════════════════════════════

    static string DensityLabel(int i) => Loc.Get(i switch
    {
        0 => Strings.Settings.Choice.Compact,
        2 => Strings.Settings.Choice.Cozy,
        3 => Strings.Settings.Choice.Comfortable,
        _ => Strings.Settings.Choice.Default,
    });

    static string TrackListStyleLabel(int i)
        => Loc.Get(i == 1 ? Strings.Settings.Appearance.TrackListClassic : Strings.Settings.Appearance.TrackListModern);

    static string PageLayoutLabel(int i)
        => Loc.Get(i == ClassicSettingsRules.PageLayoutHero ? Strings.Settings.Choice.Hero : Strings.Settings.Choice.Automatic);

    /// <summary>The miniature's row height and art edge are the REAL Modern ladder × the tested preview scale (N6).</summary>
    static Element DensityCard(int value, bool on)
    {
        var rows = ClassicDensity.Rows((RowDensity)value);
        return Controls.PickerTitled(
            Controls.PickerCard(on, Controls.PickerTile, Controls.PickerDensityRows(
                rows.ModernRow * ClassicDensity.PreviewScale, rows.ModernArt * ClassicDensity.PreviewScale, on)),
            DensityLabel(value), on);
    }

    /// <summary>UI-native miniature rows, never screenshots: Modern = the art-led stacked row, Classic = three lanes + a
    /// hairline; both inherit the live theme and the card's selected ink.</summary>
    static Element TrackListStyleCard(int value, bool on)
    {
        var ink = Controls.PickerInk.For(on);
        Element RowOf() => value == 0 ? Controls.PickerModernRow(ink) : Controls.PickerClassicRow(ink);
        return Controls.PickerTitled(
            Controls.PickerCard(on, Controls.PickerTile, RowOf(), RowOf(), RowOf()) with { Justify = FlexJustify.Center },
            TrackListStyleLabel(value), on);
    }

    /// <summary>The "Daylist clock" combo (#185): Countdown and update time · Update time only · Countdown only, in
    /// <see cref="DaylistClockMode"/>'s order — the index IS the store. Its own component so the frozen combo's signal
    /// follows the appearance epoch; the write goes through the ONE appearance writer, so the playlist hero strip and the
    /// Home card swap their clock live, with no page bump (nothing else on this tab reads it).</summary>
    sealed class DaylistClockPicker : Component
    {
        public override Element Render()
        {
            UseSignalEffect(static () => s_daylistClock.Value = D(Tab.Appearance, "daylistClock").Current is int clock ? clock : (int)DaylistClockMode.Both);
            return ComboBox.Create(
                [
                    Loc.Get(Strings.Settings.Appearance.DaylistClockBoth),
                    Loc.Get(Strings.Settings.Appearance.DaylistClockUpdateTime),
                    Loc.Get(Strings.Settings.Appearance.DaylistClockCountdown),
                ],
                s_daylistClock, width: ClassicSettingsRules.StageComboWidth, onChange: static i => D(Tab.Appearance, "daylistClock").Writer.Choose!(i));
        }
    }

    /// <summary>A wireframe of the page SYSTEM each choice selects — Automatic: a narrow metadata rail beside full-width
    /// rows; Hero: an artwork field and compact identity above the rows (ch 27 W4).</summary>
    static Element PageLayoutCard(int value, bool on)
    {
        var ink = Controls.PickerInk.For(on);

        Element Bar(float w, float h) => new BoxEl { Width = w, Height = h, Corners = CornerRadius4.All(h / 2f), Fill = ink.Faint };
        Element RowBar() => new BoxEl { Height = ClassicSettingsRules.SketchRowBarHeight, AlignSelf = FlexAlign.Stretch, Corners = CornerRadius4.All(2f), Fill = ink.Faint };
        Element Block(float w, float h) => new BoxEl { Width = w, Height = h, Shrink = 0f, Corners = CornerRadius4.All(4f), Fill = ink.Block };

        Element sketch = value == ClassicSettingsRules.PageLayoutAutomatic
            ? new BoxEl
            {
                Direction = 0, Gap = ClassicSettingsRules.SketchColumnGap, Grow = 1f, AlignItems = FlexAlign.Stretch,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 1, Gap = ClassicSettingsRules.SketchTightGap, Shrink = 0f, Justify = FlexJustify.Center,
                        Children = [Block(20f, 20f), Bar(30f, 6f), Bar(22f, 4f), Block(20f, 8f)],
                    },
                    new BoxEl { Direction = 1, Gap = ClassicSettingsRules.SketchGap, Grow = 1f, Justify = FlexJustify.Center, Children = [RowBar(), RowBar(), RowBar(), RowBar()] },
                ],
            }
            : new BoxEl
            {
                Direction = 1, Gap = ClassicSettingsRules.SketchGap, Grow = 1f, Justify = FlexJustify.Center,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 1, Gap = ClassicSettingsRules.SketchTightGap, AlignItems = FlexAlign.Stretch,
                        Children =
                        [
                            new BoxEl { Height = ClassicSettingsRules.SketchHeroHeight, AlignSelf = FlexAlign.Stretch, Corners = CornerRadius4.All(4f), Fill = ink.Block },
                            new BoxEl
                            {
                                Direction = 0, Gap = ClassicSettingsRules.SketchGap, AlignItems = FlexAlign.Center,
                                Children =
                                [
                                    Bar(48f, 6f), Bar(28f, 4f),
                                    new BoxEl { Direction = 0, Gap = ClassicSettingsRules.SketchTightGap, Children = [Block(24f, 8f), Block(24f, 8f)] },
                                ],
                            },
                        ],
                    },
                    RowBar(), RowBar(), RowBar(),
                ],
            };

        return Controls.PickerTitled(Controls.PickerCard(on, Controls.PickerTile, sketch), PageLayoutLabel(value), on);
    }

    // ══ 5. SIDEBAR (G-182; ch 25 W21 compact cards, ch 27 W2/W29 ②) ═════════════════════════════════════════════════

    /// <summary>Default · Compact (design P.4).</summary>
    sealed class SidebarDensityPicker : Component
    {
        static readonly Signal<int> s_index = new(0);
        public override Element Render()
        {
            int index = (int)Sidebar.Density.Peek();
            UseEffect(() => s_index.Value = index, DepKey.From(index));
            return ComboBox.Create([Loc.Get("sidebar.density.default"), Loc.Get("sidebar.density.compact")], s_index, width: ClassicSettingsRules.ZoomComboWidth,
                onChange: static i => D(Tab.Appearance, "sidebarDensity").Writer.Choose!(i));
        }
    }

    /// <summary>The two layout cards (Classic · Library), applied at once; disabled while the sidebar is being edited
    /// (design Q7). The setup wizard's Layout step mounts them through <see cref="ViewKeys.SettingsSidebarLayoutCards"/> (P4).</summary>
    static Element SidebarLayoutCards(SidebarLayoutId active, bool editing)
    {
        var strip = Controls.PickerStrip(2, (int)active,
            static (i, on) => SidebarLayoutCardFace((SidebarLayoutId)i, on),
            editing ? static _ => { } : static i => D(Tab.Appearance, "sidebarDesign").Writer.Choose!(i));
        if (!editing) return strip;
        // Q7: never a silent dead control — the cards say why they do not switch (the row's reason).
        return new BoxEl
        {
            Direction = 1, Gap = ClassicSettingsRules.SidebarNoteGap,
            Children = [strip, Ui.Caption(Loc.Get(SettingsDoc.FinishEditingKey)) with { Color = Tok.TextSecondary }],
        };
    }

    static Element SidebarLayoutCardFace(SidebarLayoutId layout, bool on)
    {
        var ink = Controls.PickerInk.For(on);
        bool library = layout == SidebarLayoutId.Library;
        Element preview = new BoxEl
        {
            Height = ClassicSettingsRules.SidebarPreviewHeight, AlignSelf = FlexAlign.Stretch, Shrink = 0f, Direction = 1, Gap = ClassicSettingsRules.SidebarPreviewGap, ClipToBounds = true,
            Padding = new Edges4(8f, 7f, 8f, 0f),   // no bottom pad: the miniature continues past the fold, like a pane
            Corners = CornerRadius4.All(6f),
            Fill = on ? Tok.AccentSubtle : Tok.FillLayerDefault,
            BorderWidth = 1f, BorderColor = on ? Tok.AccentDefault : Tok.StrokeCardDefault,
            Children = SidebarPreview(layout, ink),
        };
        var title = Controls.PickerLabel(Sidebar.LayoutName(layout), on);
        Element titleRow = new BoxEl
        {
            Direction = 0, Gap = ClassicSettingsRules.SidebarNoteGap, AlignItems = FlexAlign.Center, AlignSelf = FlexAlign.Stretch,
            // The "Active" tag makes the selection survive a colour-blind read.
            Children = on ? [title with { Shrink = 1f }, ActiveTag()] : [title],
        };
        return Controls.PickerCard(on, Controls.PickerPaneCompact, preview, titleRow,
            R.Type.MicroMeta(Loc.Get(library ? "sidebar.layoutName.librarySub" : "sidebar.layoutName.classicSub")) with
            {
                Color = Tok.TextTertiary, Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.WordEllipsis,
                AlignSelf = FlexAlign.Stretch,
            }) with { Key = library ? "library" : "classic" };
    }

    static Element ActiveTag() => new BoxEl
    {
        Height = Spacing.L, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f), Corners = Radii.PillAll, Fill = Tok.AccentDefault,
        Children = [new TextEl(Loc.Get(Strings.Sidebar.Active)) { Size = ClassicSettingsRules.TagSize, Weight = 600, Color = Tok.TextOnAccentPrimary, MaxLines = 1 }],
    };

    /// <summary>Static semantic geometry at quarter scale (the compact counts) — never a mounted sidebar, never text.</summary>
    static Element[] SidebarPreview(SidebarLayoutId layout, Controls.PickerInk ink)
    {
        var kids = new List<Element>(9);
        if (layout == SidebarLayoutId.Library)   // the filter chip strip, the sort pill, then the unified list
        {
            kids.Add(new BoxEl
            {
                Direction = 0, Gap = ClassicSettingsRules.SidebarMiniGap, Shrink = 0f,
                Children = [MiniBar(26f, 9f, ink.Block), MiniBar(20f, 9f, ink.Faint), MiniBar(24f, 9f, ink.Faint), MiniBar(18f, 9f, ink.Faint)],
            });
            kids.Add(new BoxEl { Direction = 0, Shrink = 0f, AlignItems = FlexAlign.Center, Children = [MiniBar(34f, 8f, ink.Faint)] });
            for (int i = 0; i < 3; i++) kids.Add(MiniArtRow(ink));
        }
        else                                     // Classic: the library shortcuts, a rule, the flat playlist list
        {
            for (int i = 0; i < 4; i++) kids.Add(MiniIconRow(i, ink));
            kids.Add(MiniHairline(ink));
            for (int i = 0; i < 2; i++) kids.Add(MiniArtRow(ink));
        }
        return kids.ToArray();
    }

    static Element MiniBar(float w, float h, ColorF fill)
        => new BoxEl { Width = w, Height = h, Shrink = 0f, Corners = CornerRadius4.All(h / 2f), Fill = fill };

    static Element MiniHairline(Controls.PickerInk ink) => new BoxEl
    {
        Height = 1f, AlignSelf = FlexAlign.Stretch, Shrink = 0f, Fill = ink.Faint,
        Margin = new Edges4(0f, Spacing.XXS, 0f, Spacing.XXS),
    };

    static Element MiniIconRow(int i, Controls.PickerInk ink) => new BoxEl
    {
        Direction = 0, Height = Spacing.S, Shrink = 0f, Gap = Spacing.XS, AlignItems = FlexAlign.Center,
        Children =
        [
            new BoxEl { Width = Spacing.S, Height = Spacing.S, Shrink = 0f, Corners = Radii.ControlAll, Fill = ink.Block },
            MiniBar(i switch { 0 => 46f, 1 => 38f, 2 => 42f, _ => 34f }, Spacing.XXS, ink.Faint),
        ],
    };

    /// <summary>One text-free list row: a 10-DIP cover tile plus two metadata bars (compact 52 / 34).</summary>
    static Element MiniArtRow(Controls.PickerInk ink) => new BoxEl
    {
        Direction = 0, Height = ClassicSettingsRules.MiniRowHeight, Shrink = 0f, Gap = ClassicSettingsRules.SketchGap, AlignItems = FlexAlign.Center,
        Children =
        [
            new BoxEl { Width = ClassicSettingsRules.MiniRowHeight, Height = ClassicSettingsRules.MiniRowHeight, Shrink = 0f, Corners = CornerRadius4.All(2.5f), Fill = ink.Block },
            new BoxEl
            {
                Direction = 1, Gap = Spacing.XXS, Grow = 1f, Basis = 0f, MinWidth = 0f,
                Children = [MiniBar(52f, Spacing.XS, ink.Faint), MiniBar(34f, Spacing.XXS, ink.Faint)],
            },
        ],
    };

    // ══ 6. LYRICS + NOW PLAYING ═══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The one lyrics-blur control: the 0..100 slider (0 turns the blur off). There is no Auto link any more.</summary>
    static Element LyricsBlurControl()
        => new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, Shrink = 0f,
            Children = [Slider.Create(s_lyricsBlurSlider, static v => SetLyricsBlur(v), s_lyricsBlurOptions, length: ClassicSettingsRules.SliderLength)],
        };

    /// <summary>Writes the 0..100 value through the row's writer, then re-seeds the slider from the document.</summary>
    static void SetLyricsBlur(float v)
    {
        var row = D(Tab.Appearance, "lyricsBlur");
        if (!row.Writer.Slide!(v)) return;
        if (row.Current is float blur) s_lyricsBlurSlider.Value = blur;
        Settings.Bump();
    }

    /// <summary>The player-style combo: all twelve presets in catalog order, index == id. The selection follows every
    /// other style writer through one effect on the now-playing epoch; the write goes through the rail's adapter (it also
    /// flips the hero to Player and logs the source) and never bumps the page.</summary>
    sealed class NpvStylePicker : Component
    {
        public override Element Render()
        {
            UseSignalEffect(static () => s_npvStyle.Value = D(Tab.Appearance, "npvStyle").Current is int style ? style : Rail.PlayerCatalog.DefaultPresetId);
            var presets = Rail.PlayerCatalog.Presets;
            var labels = new string[presets.Length];
            for (int i = 0; i < presets.Length; i++) labels[i] = Loc.Get(presets[i].LabelKey);
            return ComboBox.Create(labels, s_npvStyle, width: ClassicSettingsRules.NpvComboWidth,
                onChange: static i => D(Tab.Appearance, "npvStyle").Writer.Choose!(i));
        }
    }

    // ══ 7. FULLSCREEN STAGE ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The shown faces' names in the gallery's order (the combo's items).</summary>
    static string[] VisualizerLabels()
    {
        var shown = Visualizer.Catalog.Shown;
        var labels = new string[shown.Length];
        for (int i = 0; i < shown.Length; i++) labels[i] = Loc.Get(Visualizer.NameKey(shown[i]));
        return labels;
    }

    sealed class StageVisualizerPicker : Component
    {
        public override Element Render()
        {
            // the gallery's pick lands here live
            UseSignalEffect(static () => s_stageVisualizer.Value = D(Tab.Appearance, "stageVisualizer").Current is int index ? index : 0);
            return ComboBox.Create(VisualizerLabels(), s_stageVisualizer, width: ClassicSettingsRules.StageComboWidth,
                onChange: static i => D(Tab.Appearance, "stageVisualizer").Writer.Choose!(i));
        }
    }
    /// <summary>The layouts' names in the persisted order (the combo's items: the index IS the stored int).</summary>
    static string[] LayoutLabels() =>
    [
        Loc.Get(Strings.Stage.Layout.Card), Loc.Get(Strings.Stage.Layout.Large), Loc.Get(Strings.Stage.Layout.Centered), Loc.Get(Strings.Stage.Layout.Artist),
    ];
    sealed class StageLayoutPicker : Component
    {
        public override Element Render()
        {
            // the options popover's and the [ ] keys' picks land here live
            UseSignalEffect(static () => { if (D(Tab.Appearance, "stageLayout").Current is int layout) s_stageLayout.SetIfChanged(layout); });
            return ComboBox.Create(LayoutLabels(), s_stageLayout, width: ClassicSettingsRules.StageComboWidth, onChange: static i => D(Tab.Appearance, "stageLayout").Writer.Choose!(i));
        }
    }
    sealed class StageSensitivitySlider : Component
    {
        public override Element Render()
        {
            UseSignalEffect(static () => { if (D(Tab.Appearance, "stageSensitivity").Current is float sensitivity) s_stageSensitivity.SetIfChanged(sensitivity); });
            return Slider.Create(s_stageSensitivity, static v => D(Tab.Appearance, "stageSensitivity").Writer.Slide!(v), s_stageSensitivityOptions, length: ClassicSettingsRules.SliderLength);
        }
    }
    sealed class StageSyncSlider : Component
    {
        public override Element Render()
        {
            UseSignalEffect(static () => { if (D(Tab.Appearance, "stageSyncOffset").Current is float offset) s_stageSyncOffset.SetIfChanged(offset); });
            return Slider.Create(s_stageSyncOffset, static v => D(Tab.Appearance, "stageSyncOffset").Writer.Slide!(v), s_stageSyncOptions, length: ClassicSettingsRules.SliderLength);
        }
    }
}
