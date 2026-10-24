// ── Themes/WinUi/Structure/Views/Screens/WinUiSettings.Appearance.cs — the Appearance tab (themes plan 8.1, T1b; T11b) ───────────────────
//
// From `Screens\Settings.UI.Appearance.cs`. Theme (the picker, `WinUiSettings.Theme.cs`) · the active theme's options (one expander, its rows grouped) ·
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

namespace Wavee.Themes.WinUi;

internal static partial class WinUiSettings
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

    private static Element[] AppearanceTab()
    {
        // The now-playing rows' ONE foreign epoch (ch 27 §7): the header row, the flyout, the art menu and the palette all
        // write it, and these two rows must follow them live.
        int npvPresentation = Rail.PlayerPrefs.Presentation();
        var npvPreset = Rail.PlayerPrefs.CurrentPreset();
        var facts = SettingsFacts.Live;   // developer mode is read reactively here, as the tab always did

        // T1b (themes plan 8.1): Theme (the picker) · the active theme's options · App mode · Zoom, then what is not a theme
        // option and has no other home yet. The picker and the options are keyed views that re-render on their own signals.
        // T1b (themes plan 8.1): Theme (the picker) · the active theme's options · App mode · Zoom, then what is not a theme option and has no other home yet. The
        // picker and the options are ONE expander card on this page (W-Settings: the options sit in the Theme card), keyed views that re-render on their own signals.
        var kids = new List<Element>(24)
        {
            Mark("Theme"),
            SectionHeader(Loc.Get(Strings.Theme.Winui.Screens.ThemeSection), null, Loc.Get(Strings.Theme.Winui.Screens.ThemeSectionSub)),
            ThemeSettingsCard(),
            Mark("Theme options"),
            // The core presentation prefs every theme honours: no header, so they read as the board's cards under the Theme card.
            Mark("Display"),
            IconRow(Loc.Get(D(Tab.Appearance, "appMode").TitleKey), DescriptionOf(D(Tab.Appearance, "appMode")), RowGlyph(Tab.Appearance, "appMode"),
                Combo(ThemeLabels(), new Signal<int>((int)Appearance.Mode), width: WinUiSettingsRules.OptionComboWidth, onChange: static m => SetTheme(m))),
            // A ComboBox, not a SelectorBar: thirteen items (Auto + twelve rungs) would be thirteen segments wide.
            IconRow(Loc.Get(D(Tab.Appearance, "zoom").TitleKey), DescriptionOf(D(Tab.Appearance, "zoom")), RowGlyph(Tab.Appearance, "zoom"),
                Embed.Comp(static () => new ZoomPicker())),
            DocRow(D(Tab.Appearance, "marquee"), RowToggle(D(Tab.Appearance, "marquee"))),
        };

        // The blur dial, then the on-device AI lyrics card (its own component: setup progress re-renders only the card).
        // The second line and the animated backdrop are no longer settings at all.
        kids.Add(Mark("Lyrics"));
        kids.Add(SectionHeader(Loc.Get(Strings.Settings.Lyrics.Title), SectionGlyph(Tab.Appearance, "Lyrics"),
            Loc.Get(Strings.Settings.Lyrics.Subtitle)));
        kids.Add(DocRow(D(Tab.Appearance, "lyricsBlur"), LyricsBlurControl()));
        kids.Add(AiLyricsCard());

        // ── the fullscreen stage: the gallery's four settings, mirrored here with the rest of Appearance ──
        kids.Add(Mark("Fullscreen"));
        kids.Add(SectionHeader(Loc.Get(Strings.Stage.Settings.Title), SectionGlyph(Tab.Appearance, "Fullscreen"), Loc.Get(Strings.Stage.Settings.Subtitle)));
        kids.Add(DocRow(D(Tab.Appearance, "stageVisualizer"), Embed.Comp(static () => new StageVisualizerPicker())));
        kids.Add(DocRow(D(Tab.Appearance, "stageLayout"), Embed.Comp(static () => new StageLayoutPicker())));
        kids.Add(DocRow(D(Tab.Appearance, "stageSensitivity"), Embed.Comp(static () => new StageSensitivitySlider())));
        kids.Add(DocRow(D(Tab.Appearance, "stageLyricsOverlay"), RowToggle(D(Tab.Appearance, "stageLyricsOverlay"))));
        kids.Add(DocRow(D(Tab.Appearance, "stageSyncOffset"), Embed.Comp(static () => new StageSyncSlider())));
        kids.Add(DocRow(D(Tab.Appearance, "stageCalm"), RowToggle(D(Tab.Appearance, "stageCalm"))));
        kids.Add(DocRow(D(Tab.Appearance, "stageMoments"), RowToggle(D(Tab.Appearance, "stageMoments"))));

        // DEVELOPER-ONLY, the whole section: the Cover/Player switch exists only in developer mode, and the player style is hidden
        // until it affects Now playing (#199), so a normal build composes neither row nor their header.
        if (D(Tab.Appearance, "npvStyle").Visible(facts))
        {
            kids.Add(Mark("Now playing"));
            kids.Add(SectionHeader(Loc.Get(Strings.Settings.NowPlaying.Title), SectionGlyph(Tab.Appearance, "Now playing"),
                Loc.Get(Strings.Settings.NowPlaying.Subtitle)));
            // The second label FOLLOWS the current style; SelectorBar re-pushes its items, so it relabels in place.
            if (D(Tab.Appearance, "npvPresentation").Visible(facts))
                kids.Add(DocRow(D(Tab.Appearance, "npvPresentation"),
                    Selector([Loc.Get(Strings.Player.PresentationCover), Loc.Get(npvPreset.ShortLabelKey)],
                        new Signal<int>(npvPresentation),
                        onChange: static i => D(Tab.Appearance, "npvPresentation").Writer.Choose!(i))));
            kids.Add(DocRow(D(Tab.Appearance, "npvStyle"), Embed.Comp(static () => new NpvStylePicker())));
        }

        kids.Add(Mark("Home"));
        kids.Add(SectionHeader(Loc.Get(Strings.Settings.Home.Title), SectionGlyph(Tab.Appearance, "Home"),
            Loc.Get(Strings.Settings.Home.Subtitle)));
        // A ComboBox in its own component, the zoom/player-style shape: the items, signal and onChange freeze at mount.
        kids.Add(DocRow(D(Tab.Appearance, "daylistClock"), Embed.Comp(static () => new DaylistClockPicker())));

        return kids.ToArray();
    }

    // ══ 3. THEME + ZOOM ═══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A document row's description (its reason while it is greyed with one, else its caption).</summary>
    static string? DescriptionOf(SettingsRow row) => row.DescriptionKey(SettingsFacts.Live) is { } key ? Loc.Get(key) : null;

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
            // The combo's ITEMS freeze at mount: the head label changes only on a plateau change, so re-key on that.
            return Combo(_labels, s_zoomIndex, width: WinUiSettingsRules.OptionComboWidth, onChange: Pick) with { Key = "appearance.zoom:" + percent };
        }

        /// <summary>A deliberate pick writes at once (the chords' debounce exists for key repeats). The head item writes
        /// mode = Auto — never Dense — and applies THIS render's suggestion; a rung writes Manual.</summary>
        void Pick(int i)
        {
            if (D(Tab.Appearance, "zoom").Writer.Choose!(i)) Settings.Bump();
        }
    }


    /// <summary>The "Daylist clock" combo (#185): Countdown and update time · Update time only · Countdown only, in
    /// <see cref="DaylistClockMode"/>'s order — the index IS the store. Its own component so the frozen combo's signal
    /// follows the appearance epoch; the write goes through the ONE appearance writer, so the playlist hero strip and the
    /// Home card swap their clock live, with no page bump (nothing else on this tab reads it).</summary>
    sealed class DaylistClockPicker : SettingsComponent
    {
        public override Element Render()
        {
            UseLayout();
            UseSignalEffect(static () => s_daylistClock.Value = D(Tab.Appearance, "daylistClock").Current is int clock ? clock : (int)DaylistClockMode.Both);
            return Combo(
                [
                    Loc.Get(Strings.Settings.Appearance.DaylistClockBoth),
                    Loc.Get(Strings.Settings.Appearance.DaylistClockUpdateTime),
                    Loc.Get(Strings.Settings.Appearance.DaylistClockCountdown),
                ],
                s_daylistClock, width: WinUiSettingsRules.StageComboWidth, onChange: static i => D(Tab.Appearance, "daylistClock").Writer.Choose!(i));
        }
    }

    // ══ 6. LYRICS + NOW PLAYING ═══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The one lyrics-blur control: the 0..100 slider (0 turns the blur off). There is no Auto link any more.</summary>
    static Element LyricsBlurControl()
        => new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, Shrink = 0f,
            Children = [SliderControl(s_lyricsBlurSlider, static v => SetLyricsBlur(v), s_lyricsBlurOptions, length: WinUiSettingsRules.SliderLength)],
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
    sealed class NpvStylePicker : SettingsComponent
    {
        public override Element Render()
        {
            UseLayout();
            UseSignalEffect(static () => s_npvStyle.Value = D(Tab.Appearance, "npvStyle").Current is int style ? style : Rail.PlayerCatalog.DefaultPresetId);
            var presets = Rail.PlayerCatalog.Presets;
            var labels = new string[presets.Length];
            for (int i = 0; i < presets.Length; i++) labels[i] = Loc.Get(presets[i].LabelKey);
            return Combo(labels, s_npvStyle, width: WinUiSettingsRules.NpvComboWidth,
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

    sealed class StageVisualizerPicker : SettingsComponent
    {
        public override Element Render()
        {
            UseLayout();
            // the gallery's pick lands here live
            UseSignalEffect(static () => s_stageVisualizer.Value = D(Tab.Appearance, "stageVisualizer").Current is int index ? index : 0);
            return Combo(VisualizerLabels(), s_stageVisualizer, width: WinUiSettingsRules.StageComboWidth,
                onChange: static i => D(Tab.Appearance, "stageVisualizer").Writer.Choose!(i));
        }
    }
    /// <summary>The layouts' names in the persisted order (the combo's items: the index IS the stored int).</summary>
    static string[] LayoutLabels() =>
    [
        Loc.Get(Strings.Stage.Layout.Card), Loc.Get(Strings.Stage.Layout.Large), Loc.Get(Strings.Stage.Layout.Centered), Loc.Get(Strings.Stage.Layout.Artist),
    ];
    sealed class StageLayoutPicker : SettingsComponent
    {
        public override Element Render()
        {
            UseLayout();
            // the options popover's and the [ ] keys' picks land here live
            UseSignalEffect(static () => { if (D(Tab.Appearance, "stageLayout").Current is int layout) s_stageLayout.SetIfChanged(layout); });
            return Combo(LayoutLabels(), s_stageLayout, width: WinUiSettingsRules.StageComboWidth, onChange: static i => D(Tab.Appearance, "stageLayout").Writer.Choose!(i));
        }
    }
    sealed class StageSensitivitySlider : SettingsComponent
    {
        public override Element Render()
        {
            UseLayout();
            UseSignalEffect(static () => { if (D(Tab.Appearance, "stageSensitivity").Current is float sensitivity) s_stageSensitivity.SetIfChanged(sensitivity); });
            return SliderControl(s_stageSensitivity, static v => D(Tab.Appearance, "stageSensitivity").Writer.Slide!(v), s_stageSensitivityOptions, length: WinUiSettingsRules.SliderLength);
        }
    }
    sealed class StageSyncSlider : SettingsComponent
    {
        public override Element Render()
        {
            UseLayout();
            UseSignalEffect(static () => { if (D(Tab.Appearance, "stageSyncOffset").Current is float offset) s_stageSyncOffset.SetIfChanged(offset); });
            return SliderControl(s_stageSyncOffset, static v => D(Tab.Appearance, "stageSyncOffset").Writer.Slide!(v), s_stageSyncOptions, length: WinUiSettingsRules.SliderLength);
        }
    }
}
