// ── Screens/Settings.UI.Appearance.cs ──────────────────────────────────────────────────────────────────────────────
// the Appearance tab (themes plan 8.1, T1b): Theme (the picker) · the active theme's options (one expander; its sidebar, track
// list and surface rows are in `Settings.UI.Theme.cs`) · App mode · Zoom · Marquee, then what is not a theme option and has no
// other home yet: Lyrics (blur, the on-device AI lyrics card) · Fullscreen (visualizer, sensitivity, lyrics overlay, sync offset,
// calm motion, change with the music — the fullscreen stage's gallery settings, mirrored through `Prefs.Stage`) · Now playing
// (hero*, player style) · Home (the daylist clock)
//
// * DEVELOPER-ONLY rows (`Catalog.RowVisible`): page motion and the Cover/‹Player› hero switch are composed away while the
// developer switch is off. "Lyrics second line" and "Animated lyrics backdrop" are not gated but GONE — neither is a
// setting any more (the backdrop is a fixed constant; the second line is session state the lyrics globe toggles own).
//
// Role: UI
// Owner: R
// Wave: 6
// Budget: 700 lines
// Spec: DERIVED (ch 27 §9.3 / ch 30 §9.4); ch 27 §0 N3-N6, §1.2 props-freeze map, W2-W4, W29, §3, §4.2, §5, §6, §10
// items 12-20; ch 30 §1.1 (which epoch each write bumps) + N12 (the row-density write reaches mounted pages); ch 25 W21
// (the design cards — no 0.3 `SidebarDesignPicker` exists, so the compact cards are built here on owner L's picker)
//
// EVERY ROW WRITES THROUGH ITS OWN EPOCH (ch 27 W2 "three facts"): marquee / washes / hide artwork /
// row density / track list style / daylist clock → `Prefs.Appearance`; page layout, rail uniform, rail reset → `Prefs.DetailHero`;
// the lyrics blur → `Prefs.Lyrics`; the two Now-playing rows → `Prefs.NpvPlayer` with NO page `Bump()` (the
// tab reads that epoch); the six Fullscreen rows → `Prefs.Stage` (the page reads that epoch too, so the stage's own
// gallery writes show here live). The tab body reads the store directly (never a foreign epoch it does not need), so a write
// re-renders this page once, through `Bump()`.
//
// FROZEN CONTROL INPUTS. A ComboBox freezes its items, its signal AND its onChange at mount: the zoom and player-style
// combos are therefore tiny child components that keep one stable signal in step through an effect, and every handler
// reads the store at invocation, never a render local. SelectorBar / Slider / CheckBox re-push their props.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;

namespace Wavee;

public static partial class Settings
{
    // ══ 1. STABLE CONTROL STATE (0.2.9's `_lyricsBlurSlider` instance field + the two combo selections) ═════════════

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
    // child component re-seeds it from Prefs.Stage on every epoch (StageVisualizerPicker / StageSensitivitySlider /
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

    static partial void SeedAppearance()
    {
        s_lyricsBlurSlider.Value = Prefs.Lyrics.BlurStrength();
        s_stageVisualizer.Value = VisualizerIndex();
        s_stageLayout.Value = Prefs.Stage.Layout();
        s_stageSensitivity.Value = Prefs.Stage.Sensitivity();
        s_stageSyncOffset.Value = Prefs.Stage.SyncOffsetMs();
    }

    // ══ 2. THE TAB ════════════════════════════════════════════════════════════════════════════════════════════════════

    private static partial Element AppearanceTab()
    {
        // The now-playing rows' ONE foreign epoch (ch 27 §7): the header row, the flyout, the art menu and the palette all
        // write it, and these two rows must follow them live.
        int npvPresentation = Rail.PlayerPrefs.Presentation();
        var npvPreset = Rail.PlayerPrefs.CurrentPreset();
        var facts = SettingsFacts.Live;   // developer mode is read reactively here, as the tab always did

        // T1b (themes plan 8.1): Theme (the picker) · the active theme's options · App mode · Zoom, then what is not a theme
        // option and has no other home yet. The picker and the options are components that re-render on their own signals.
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
        kids.Add(AiLyrics.SettingsCard());

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

    /// <summary>0 System · 1 Light · 2 Dark: <see cref="Appearance.SetMode"/> swaps the installed Wavee palette IN PLACE
    /// (System re-reads the OS theme and accent now), persists, and asks the host for the 250 ms re-theme cross-fade
    /// (ch 27 §5); this page then re-reads its rows.</summary>
    static void SetTheme(int mode)
    {
        if (D(Tab.Appearance, "appMode").Writer.Choose!(mode)) Bump();
    }

    /// <summary>Ordered to match the stored value (<see cref="Design.PageMotionStyle"/>'s declaration order) — the
    /// index IS the store.</summary>
    static string[] PageMotionLabels() =>
    [
        Loc.Get(Strings.Settings.Appearance.PageMotionFluent),
        Loc.Get(Strings.Settings.Appearance.PageMotionSpatial),
        Loc.Get(Strings.Settings.Appearance.PageMotionWinUi),
        Loc.Get(Strings.Settings.Appearance.PageMotionClassic),
        Loc.Get(Strings.Settings.Appearance.PageMotionNone),
    ];

    /// <summary>Persist the page-motion style through the ONE Appearance writer (bumps the epoch once), then bump the
    /// page. Every mounted content host re-reads it on its NEXT swap — no restart, no re-navigation required to try a
    /// style, same live-apply contract as row density (ch 30 N12).</summary>
    static void SetPageMotionStyle(int i)
    {
        if (D(Tab.Appearance, "pageMotion").Writer.Choose!(i)) Bump();
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
            return ComboBox.Create(_labels, s_zoomIndex, width: 160f, onChange: Pick) with { Key = "appearance.zoom:" + percent };
        }

        /// <summary>A deliberate pick writes at once (the chords' debounce exists for key repeats). The head item writes
        /// mode = Auto — never Dense — and applies THIS render's suggestion; a rung writes Manual.</summary>
        void Pick(int i)
        {
            if (D(Tab.Appearance, "zoom").Writer.Choose!(i)) Bump();
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
        => Loc.Get(i == Prefs.DetailHero.Hero ? Strings.Settings.Choice.Hero : Strings.Settings.Choice.Automatic);

    /// <summary>The miniature's row height and art edge are the REAL Modern ladder × the tested preview scale (N6).</summary>
    static Element DensityCard(int value, bool on)
        => Controls.PickerTitled(
            Controls.PickerCard(on, Controls.PickerTile, Controls.PickerDensityRows(
                Track.TableRules.RowHeightFor(value, classic: false) * Track.TableRules.PreviewScale,
                Track.TableRules.ArtSizeFor(value, classic: false) * Track.TableRules.PreviewScale, on)),
            DensityLabel(value), on);

    /// <summary>ch 30 N12, NOT ported: the Settings density write bumps the appearance epoch, so a mounted or parked track
    /// list re-reads it. Called on every keyboard rove too, so an unchanged value writes nothing.</summary>
    static void SetDensity(int i)
    {
        if (D(Tab.Appearance, "rowDensity").Writer.Choose!(i)) Bump();
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
            UseSignalEffect(static () => s_daylistClock.Value = (int)Prefs.Appearance.DaylistClock());
            return ComboBox.Create(
                [
                    Loc.Get(Strings.Settings.Appearance.DaylistClockBoth),
                    Loc.Get(Strings.Settings.Appearance.DaylistClockUpdateTime),
                    Loc.Get(Strings.Settings.Appearance.DaylistClockCountdown),
                ],
                s_daylistClock, width: 220f, onChange: static i => D(Tab.Appearance, "daylistClock").Writer.Choose!(i));
        }
    }

    /// <summary>The reset's enable gate — a destructive-looking no-op is never offered (DetailRailPrefs owns the rule).
    /// </summary>
    internal static bool HasCustomizedRail() => DetailRailPrefs.HasCustomized();

    /// <summary>A wireframe of the page SYSTEM each choice selects — Automatic: a narrow metadata rail beside full-width
    /// rows; Hero: an artwork field and compact identity above the rows (ch 27 W4).</summary>
    static Element PageLayoutCard(int value, bool on)
    {
        var ink = Controls.PickerInk.For(on);

        Element Bar(float w, float h) => new BoxEl { Width = w, Height = h, Corners = CornerRadius4.All(h / 2f), Fill = ink.Faint };
        Element RowBar() => new BoxEl { Height = 4f, AlignSelf = FlexAlign.Stretch, Corners = CornerRadius4.All(2f), Fill = ink.Faint };
        Element Block(float w, float h) => new BoxEl { Width = w, Height = h, Shrink = 0f, Corners = CornerRadius4.All(4f), Fill = ink.Block };

        Element sketch = value == Prefs.DetailHero.Automatic
            ? new BoxEl
            {
                Direction = 0, Gap = 8f, Grow = 1f, AlignItems = FlexAlign.Stretch,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 1, Gap = 4f, Shrink = 0f, Justify = FlexJustify.Center,
                        Children = [Block(20f, 20f), Bar(30f, 6f), Bar(22f, 4f), Block(20f, 8f)],
                    },
                    new BoxEl { Direction = 1, Gap = 5f, Grow = 1f, Justify = FlexJustify.Center, Children = [RowBar(), RowBar(), RowBar(), RowBar()] },
                ],
            }
            : new BoxEl
            {
                Direction = 1, Gap = 5f, Grow = 1f, Justify = FlexJustify.Center,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 1, Gap = 4f, AlignItems = FlexAlign.Stretch,
                        Children =
                        [
                            new BoxEl { Height = 24f, AlignSelf = FlexAlign.Stretch, Corners = CornerRadius4.All(4f), Fill = ink.Block },
                            new BoxEl
                            {
                                Direction = 0, Gap = 5f, AlignItems = FlexAlign.Center,
                                Children =
                                [
                                    Bar(48f, 6f), Bar(28f, 4f),
                                    new BoxEl { Direction = 0, Gap = 4f, Children = [Block(24f, 8f), Block(24f, 8f)] },
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
            return ComboBox.Create([Loc.Get("sidebar.density.default"), Loc.Get("sidebar.density.compact")], s_index, width: 160f,
                onChange: static i => D(Tab.Appearance, "sidebarDensity").Writer.Choose!(i));
        }
    }

    /// <summary>The two layout cards (Classic · Library), applied at once; disabled while the sidebar is being edited
    /// (design Q7). Public: the setup wizard's Layout step reuses them (P4).</summary>
    public static Element SidebarLayoutCards(SidebarLayoutId active, bool editing)
    {
        var strip = Controls.PickerStrip(2, (int)active,
            static (i, on) => SidebarLayoutCardFace((SidebarLayoutId)i, on),
            editing ? static _ => { } : static i => D(Tab.Appearance, "sidebarDesign").Writer.Choose!(i));
        if (!editing) return strip;
        // Q7: never a silent dead control — the cards say why they do not switch (the row's reason).
        return new BoxEl
        {
            Direction = 1, Gap = 6f,
            Children = [strip, Ui.Caption(Loc.Get(SettingsDoc.FinishEditingKey)) with { Color = Tok.TextSecondary }],
        };
    }

    static Element SidebarLayoutCardFace(SidebarLayoutId layout, bool on)
    {
        var ink = Controls.PickerInk.For(on);
        bool library = layout == SidebarLayoutId.Library;
        Element preview = new BoxEl
        {
            Height = 96f, AlignSelf = FlexAlign.Stretch, Shrink = 0f, Direction = 1, Gap = 3f, ClipToBounds = true,
            Padding = new Edges4(8f, 7f, 8f, 0f),   // no bottom pad: the miniature continues past the fold, like a pane
            Corners = CornerRadius4.All(6f),
            Fill = on ? Tok.AccentSubtle : Tok.FillLayerDefault,
            BorderWidth = 1f, BorderColor = on ? Tok.AccentDefault : Tok.StrokeCardDefault,
            Children = SidebarPreview(layout, ink),
        };
        var title = Controls.PickerLabel(Sidebar.LayoutName(layout), on);
        Element titleRow = new BoxEl
        {
            Direction = 0, Gap = 6f, AlignItems = FlexAlign.Center, AlignSelf = FlexAlign.Stretch,
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
        Children = [new TextEl(Loc.Get(Strings.Sidebar.Active)) { Size = 9f, Weight = 600, Color = Tok.TextOnAccentPrimary, MaxLines = 1 }],
    };

    /// <summary>Static semantic geometry at quarter scale (the compact counts) — never a mounted sidebar, never text.</summary>
    static Element[] SidebarPreview(SidebarLayoutId layout, Controls.PickerInk ink)
    {
        var kids = new List<Element>(9);
        if (layout == SidebarLayoutId.Library)   // the filter chip strip, the sort pill, then the unified list
        {
            kids.Add(new BoxEl
            {
                Direction = 0, Gap = 4f, Shrink = 0f,
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
        Direction = 0, Height = 10f, Shrink = 0f, Gap = 5f, AlignItems = FlexAlign.Center,
        Children =
        [
            new BoxEl { Width = 10f, Height = 10f, Shrink = 0f, Corners = CornerRadius4.All(2.5f), Fill = ink.Block },
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
            Children = [Slider.Create(s_lyricsBlurSlider, static v => SetLyricsBlur(v), s_lyricsBlurOptions, length: 180f)],
        };

    /// <summary>Writes the 0..100 value through the row's writer, then re-seeds the slider from the accessor.</summary>
    static void SetLyricsBlur(float v)
    {
        if (!D(Tab.Appearance, "lyricsBlur").Writer.Slide!(v)) return;
        s_lyricsBlurSlider.Value = Prefs.Lyrics.BlurStrength();
        Bump();
    }

    /// <summary>The player-style combo: all twelve presets in catalog order, index == id. The selection follows every
    /// other style writer through one effect on the now-playing epoch; the write goes through the rail's adapter (it also
    /// flips the hero to Player and logs the source) and never bumps the page.</summary>
    sealed class NpvStylePicker : Component
    {
        public override Element Render()
        {
            UseSignalEffect(static () => s_npvStyle.Value = Rail.PlayerPrefs.Style());
            var presets = Rail.PlayerCatalog.Presets;
            var labels = new string[presets.Length];
            for (int i = 0; i < presets.Length; i++) labels[i] = Loc.Get(presets[i].LabelKey);
            return ComboBox.Create(labels, s_npvStyle, width: 180f,
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

    /// <summary>The stored face (already migrated by Prefs.Stage) as its index in the combo.</summary>
    internal static int VisualizerIndex() => Math.Max(0, Visualizer.Catalog.IndexOf((Visualizer.Kind)Prefs.Stage.Visualizer()));

    sealed class StageVisualizerPicker : Component
    {
        public override Element Render()
        {
            UseSignalEffect(static () => s_stageVisualizer.Value = VisualizerIndex());   // the gallery's pick lands here live
            return ComboBox.Create(VisualizerLabels(), s_stageVisualizer, width: 220f,
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
            UseSignalEffect(static () => s_stageLayout.SetIfChanged(Prefs.Stage.Layout()));   // the options popover's and the [ ] keys' picks land here live
            return ComboBox.Create(LayoutLabels(), s_stageLayout, width: 220f, onChange: static i => D(Tab.Appearance, "stageLayout").Writer.Choose!(i));
        }
    }
    sealed class StageSensitivitySlider : Component
    {
        public override Element Render()
        {
            UseSignalEffect(static () => s_stageSensitivity.SetIfChanged(Prefs.Stage.Sensitivity()));
            return Slider.Create(s_stageSensitivity, static v => D(Tab.Appearance, "stageSensitivity").Writer.Slide!(v), s_stageSensitivityOptions, length: 180f);
        }
    }
    sealed class StageSyncSlider : Component
    {
        public override Element Render()
        {
            UseSignalEffect(static () => s_stageSyncOffset.SetIfChanged(Prefs.Stage.SyncOffsetMs()));
            return Slider.Create(s_stageSyncOffset, static v => D(Tab.Appearance, "stageSyncOffset").Writer.Slide!(v), s_stageSyncOptions, length: 180f);
        }
    }
}
