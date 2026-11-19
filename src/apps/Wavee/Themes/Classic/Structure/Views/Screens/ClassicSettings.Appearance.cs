// ── Themes/Classic/Structure/Views/Screens/ClassicSettings.Appearance.cs — Appearance's and Lyrics & Now playing's own controls (settings rework 7.1) ──
//
// From `Screens\Settings.UI.Appearance.cs`. The controls the generic grammar cannot draw: the Zoom combo ("Auto (N%)" with the LIVE resolved percent, then the
// twelve rungs), the lyrics blur slider, the full screen's sensitivity, sync and photo-dimming sliders and the spectrum's segments, the episode speed; and the
// preview faces the theme options and the setup wizard draw (row density, track-list style, page layout, surfaces, the sidebar layout cards). Every control
// writes through its dial's writer, which bumps the epoch it always bumped (ch 27 W2 "three facts"), so a write re-renders this page once.
//
// FROZEN CONTROL INPUTS. A ComboBox freezes its items, its signal AND its onChange at mount, a Slider its signal: the zoom combo and the sliders are tiny child
// components that keep one stable signal in step through an effect, and every handler reads the store at invocation (through the dial), never a render local.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

internal static partial class ClassicSettings
{
    // ══ 1. STABLE CONTROL STATE (0.2.9's `_lyricsBlurSlider` instance field + the combo selections) ═════════════════

    /// <summary>The lyrics-blur slider's value (the resolved 0..100). A slider binds ONE signal for its life.</summary>
    static readonly FloatSignal s_lyricsBlurSlider = new(Platform.Keys.LyricsBlurStrength.Default);
    /// <summary>The zoom combo's selection, kept in step with the LIVE zoom by <see cref="ZoomPicker"/>.</summary>
    static readonly Signal<int> s_zoomIndex = new(0);

    static readonly Slider.SliderOptions s_lyricsBlurOptions = new()
    {
        Min = 0f, Max = 100f, Step = 1f, TickFrequency = 25f, IsThumbToolTipEnabled = true,
        ThumbToolTipValueConverter = static v => ((int)MathF.Round(v)).ToString(System.Globalization.CultureInfo.InvariantCulture) + "%",
    };

    // The full screen's value controls the generic grammar cannot draw (the setup wizard's face combo, the sliders). Each is bound for the control's life, so
    // a child component re-seeds it from its dial on every epoch (StageVisualizerPicker / StageSensitivitySlider / StageSyncSlider / StageDimSlider below) and a
    // pick made in the stage's gallery shows here live. The combo's value is an INDEX into Visualizer.Catalog.Shown (the gallery's order), never the Kind int.
    static readonly Signal<int> s_stageVisualizer = new(Visualizer.Catalog.IndexOf(Visualizer.Catalog.Default));
    static readonly FloatSignal s_stageSensitivity = new(1f), s_stageSyncOffset = new(0f), s_heroDim = new(0f), s_episodeSpeed = new(1f);
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
    /// <summary>The Artist layout's photo dimming: the options popover's own range and step.</summary>
    static readonly Slider.SliderOptions s_heroDimOptions = new()
    {
        Min = ClassicStageOptionsGeometry.DimMin, Max = ClassicStageOptionsGeometry.DimMax, Step = ClassicStageOptionsGeometry.DimStep,
        TickFrequency = (ClassicStageOptionsGeometry.DimMax - ClassicStageOptionsGeometry.DimMin) / 4f, IsThumbToolTipEnabled = true,
        ThumbToolTipValueConverter = static v => ((int)MathF.Round(v)).ToString(System.Globalization.CultureInfo.InvariantCulture) + "%",
    };
    /// <summary>The podcast and audiobook speed: the player bar's speed panel's own range and steps.</summary>
    static readonly Slider.SliderOptions s_episodeSpeedOptions = new()
    {
        Min = ClassicPlayerBarLayout.SpeedMin, Max = ClassicPlayerBarLayout.SpeedMax, Step = ClassicPlayerBarLayout.SpeedStep,
        SmallChange = ClassicPlayerBarLayout.SpeedStep, LargeChange = ClassicPlayerBarLayout.SpeedLargeStep,
        TickFrequency = ClassicPlayerBarLayout.SpeedLargeStep * 2f, IsThumbToolTipEnabled = true,
        ThumbToolTipValueConverter = static v => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "\u00D7",
    };

    /// <summary>The ladder rungs as labels, hoisted once — digits and '%' never change with culture.</summary>
    static readonly string[] s_zoomRungLabels = BuildZoomRungLabels();

    /// <summary>The page mount: every frozen control's signal from its dial's value (`dial.Current`).</summary>
    static void SeedAppearance()
    {
        s_lyricsBlurSlider.Value = Dial("lyrics.blur").Current is float blur ? blur : s_lyricsBlurSlider.Value;
        s_stageVisualizer.Value = Dial("lyrics.stage.visualizer").Current is int visualizer ? visualizer : s_stageVisualizer.Value;
        s_stageSensitivity.Value = Dial("lyrics.stage.sensitivity").Current is float sensitivity ? sensitivity : s_stageSensitivity.Value;
        s_stageSyncOffset.Value = Dial("lyrics.stage.syncOffset").Current is float offset ? offset : s_stageSyncOffset.Value;
        s_heroDim.Value = Dial("lyrics.stage.heroDim").Current is float dim ? dim : s_heroDim.Value;
        s_episodeSpeed.Value = Dial("playback.episodeSpeed").Current is float speed ? speed : s_episodeSpeed.Value;
    }

    // ══ 3. THEME + ZOOM ═══════════════════════════════════════════════════════════════════════════════════════════════

    static string[] ThemeLabels() =>
    [
        Loc.Get(Strings.Settings.Choice.System),
        Loc.Get(Strings.Settings.Choice.Light),
        Loc.Get(Strings.Settings.Choice.Dark),
    ];

    /// <summary>0 System · 1 Light · 2 Dark: the dial's writer swaps the installed Wavee palette IN PLACE (System re-reads the OS theme and
    /// accent now), persists, and asks the host for the 250 ms re-theme cross-fade (ch 27 §5); this page then re-reads its rows. The setup wizard's
    /// options draw it (a theme option that refers to App mode).</summary>
    static void SetTheme(int mode)
    {
        if (Dial("appearance.appMode").Writer.Choose!(mode)) Settings.Bump();
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
    sealed class ZoomPicker : SettingsComponent
    {
        float _auto = 1f;
        int _labelsPercent = -1;
        string[] _labels = [];

        public override Element Render()
        {
            UseLayout();
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
            // The combo's ITEMS freeze at mount: the head label changes only on a plateau change, so re-key on that (inside a box: a component's
            // root ignores its key).
            return new BoxEl
            {
                Direction = 1, MinWidth = 0f, Grow = s_layout.Touch ? 1f : 0f, AlignSelf = s_layout.Touch ? FlexAlign.Stretch : FlexAlign.Auto,
                Children = [Combo(_labels, s_zoomIndex, width: ClassicSettingsRules.ZoomComboWidth, onChange: Pick) with { Key = "appearance.zoom:" + percent }],
            };
        }

        /// <summary>A deliberate pick writes at once (the chords' debounce exists for key repeats). The head item writes
        /// mode = Auto — never Dense — and applies THIS render's suggestion; a rung writes Manual.</summary>
        void Pick(int i)
        {
            if (Dial("appearance.zoom").Writer.Choose!(i)) Settings.Bump();
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

    /// <summary>A Surfaces level as a preview (CT9, U1; board C-Settings): a chrome strip over two page lines, the strip tinted with a sample artwork
    /// hue at the level's strength (none at Off). The strength comes from the style's own tint pair, so Rich previews the real step from Subtle;
    /// the preview never depends on the page behind Settings, which claims the neutral ground.</summary>
    static Element SurfacesCard(int value, string label, bool on)
    {
        var ink = Controls.PickerInk.For(on);
        var level = (SurfaceWash)value;
        Element Line(ColorF fill) => new BoxEl
        {
            Height = ClassicSettingsRules.SurfacesLineHeight, AlignSelf = FlexAlign.Stretch, Corners = CornerRadius4.All(2f), Fill = fill,
        };
        ColorF strip = ink.Faint;
        if (ClassicStructure.WashesOn(level))
        {
            bool rich = ClassicStructure.RichWashes(level);
            float alpha = ClassicSettingsRules.SurfacesPreviewAlpha * R.Wash.ShellTintAlphaAt(rich) / R.Wash.ShellTintAlphaAt(false);
            strip = ClassicSettingsRules.SurfacesSampleHue with { A = alpha };
        }
        return Controls.PickerTitled(
            Controls.PickerCard(on, Controls.PickerTile,
                new BoxEl
                {
                    Direction = 1, Gap = ClassicSettingsRules.SurfacesLineGap, Grow = 1f, Justify = FlexJustify.Center,
                    Children = [Line(strip), Line(ink.Faint), Line(ink.Faint)],
                }),
            label, on);
    }

    // ══ 5. SIDEBAR (G-182; ch 25 W21 compact cards, ch 27 W2/W29 ②) ═════════════════════════════════════════════════

    /// <summary>The two layout cards (Classic · Library), applied at once through the sidebar's verb (which stores the choice in the structure's option);
    /// disabled while the sidebar is being edited (design Q7). The setup wizard's Theme step shows them through the options' generated row.</summary>
    static Element SidebarLayoutCards(SidebarLayoutId active, bool editing)
    {
        var strip = Controls.PickerStrip(2, (int)active,
            static (i, on) => SidebarLayoutCardFace((SidebarLayoutId)i, on),
            editing ? static _ => { } : static i => Sidebar.SwitchLayout((SidebarLayoutId)i));
        if (!editing) return strip;
        // Q7: never a silent dead control — the cards say why they do not switch (the row's reason).
        return new BoxEl
        {
            Direction = 1, Gap = ClassicSettingsRules.SidebarNoteGap,
            Children = [strip, Ui.Caption(Loc.Get(SettingsSpec.FinishEditingKey)) with { Color = Tok.TextSecondary }],
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

    static Element ActiveTag() => Tag(Loc.Get(Strings.Sidebar.Active), Tok.AccentDefault, Tok.TextOnAccentPrimary);

    /// <summary>A pill tag (Active, Developer): its word at the type ramp's smallest rung (MicroMeta 11/15, semibold; it was 9, under the type floor),
    /// the pill at least the tuned 16 tall and as tall as its word needs.</summary>
    internal static Element Tag(string text, ColorF fill, ColorF ink) => new BoxEl
    {
        MinHeight = Spacing.L, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f), Corners = Radii.PillAll, Fill = fill,
        Children = [R.Type.MicroMeta(text) with { Weight = 600, Color = ink, MaxLines = 1 }],
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

    // ══ 6. LYRICS ═══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The one lyrics-blur control: the 0..100 slider (0 turns the blur off). There is no Auto link any more.</summary>
    static Element LyricsBlurControl()
        => new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, Shrink = 0f,
            Children = [SliderControl(s_lyricsBlurSlider, static v => SetLyricsBlur(v), s_lyricsBlurOptions, length: ClassicSettingsRules.SliderLength)],
        };

    /// <summary>Writes the 0..100 value through the dial's writer, then re-seeds the slider from the dial.</summary>
    static void SetLyricsBlur(float v)
    {
        var row = Dial("lyrics.blur");
        if (!row.Writer.Slide!(v)) return;
        if (row.Current is float blur) s_lyricsBlurSlider.Value = blur;
        Settings.Bump();
    }

    // ══ 7. FULL SCREEN ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The shown faces' names in the gallery's order (the combo's items).</summary>
    static string[] VisualizerLabels()
    {
        var shown = Visualizer.Catalog.Shown;
        var labels = new string[shown.Length];
        for (int i = 0; i < shown.Length; i++) labels[i] = Loc.Get(Visualizer.NameKey(shown[i]));
        return labels;
    }

    sealed class StageVisualizerPicker : SettingsComponent
    {
        public override Element Render()
        {
            UseLayout();
            // the gallery's pick lands here live
            UseSignalEffect(static () => s_stageVisualizer.Value = Dial("lyrics.stage.visualizer").Current is int index ? index : 0);
            return Combo(VisualizerLabels(), s_stageVisualizer, width: ClassicSettingsRules.StageComboWidth,
                onChange: static i => Dial("lyrics.stage.visualizer").Writer.Choose!(i));
        }
    }
    sealed class StageSensitivitySlider : SettingsComponent
    {
        public override Element Render()
        {
            UseLayout();
            UseSignalEffect(static () => { if (Dial("lyrics.stage.sensitivity").Current is float sensitivity) s_stageSensitivity.SetIfChanged(sensitivity); });
            return SliderControl(s_stageSensitivity, static v => Dial("lyrics.stage.sensitivity").Writer.Slide!(v), s_stageSensitivityOptions, length: ClassicSettingsRules.SliderLength);
        }
    }
    sealed class StageSyncSlider : SettingsComponent
    {
        public override Element Render()
        {
            UseLayout();
            UseSignalEffect(static () => { if (Dial("lyrics.stage.syncOffset").Current is float offset) s_stageSyncOffset.SetIfChanged(offset); });
            return SliderControl(s_stageSyncOffset, static v => Dial("lyrics.stage.syncOffset").Writer.Slide!(v), s_stageSyncOptions, length: ClassicSettingsRules.SliderLength);
        }
    }

    /// <summary>The Artist layout's photo dimming (the options popover's slider): snapped to its step; the stage's own pick lands here live.</summary>
    sealed class StageDimSlider : SettingsComponent
    {
        public override Element Render()
        {
            UseLayout();
            UseSignalEffect(static () => { if (Dial("lyrics.stage.heroDim").Current is float dim) s_heroDim.SetIfChanged(dim); });
            return SliderControl(s_heroDim, static v => Dial("lyrics.stage.heroDim").Writer.Slide!(v), s_heroDimOptions, length: ClassicSettingsRules.SliderLength);
        }
    }

    /// <summary>The spectrum under the cover (Large art, Centered): the options popover's four segments, the index IS the stored style.</summary>
    static Element SpectrumItem(SettingsBodyRow row)
    {
        SettingsDial dial = row.Dial;
        string[] labels =
        [
            Loc.Get(Strings.Stage.Spectrum.Bars), Loc.Get(Strings.Stage.Spectrum.Ring), Loc.Get(Strings.Stage.Spectrum.Line), Loc.Get(Strings.Stage.Spectrum.Off),
        ];
        return Item(row.Label, row.Description, Badged(row, Selector(labels, new Signal<int>(row.Value is int at ? at : 0), onChange: i =>
        {
            if (dial.Writer.Choose!(i)) Settings.Bump();
        })), isEnabled: row.Enabled, icon: GlyphOf(dial));
    }

    // ══ 8. PODCASTS AND AUDIOBOOKS ════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The podcast and audiobook speed: the speed menus' writer (the account's speed, synced to every device; a song always plays at 1x). The player
    /// bar's own change lands here live.</summary>
    sealed class EpisodeSpeedSlider : SettingsComponent
    {
        public override Element Render()
        {
            UseLayout();
            UseSignalEffect(static () => { if (Dial("playback.episodeSpeed").Current is float speed) s_episodeSpeed.SetIfChanged(speed); });
            return SliderControl(s_episodeSpeed, static v => Dial("playback.episodeSpeed").Writer.Slide!(v), s_episodeSpeedOptions, length: ClassicSettingsRules.SliderLength);
        }
    }
}
