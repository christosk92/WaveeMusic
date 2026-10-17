// ── Themes/Zune/Structure/Views/Screens/ZuneSettings.Appearance.cs — the Appearance tab (themes plan 6.3, Z8b) ────────────────────────────────
//
// Board Z-Settings-Appearance: the theme picker, the active theme's options, then what is not a theme option: App mode and Zoom (the core presentation prefs every
// theme honours), Marquee, Lyrics (blur, the on-device AI lyrics), Fullscreen (the stage's gallery settings mirrored through the document), Now playing and Home.
// The picker and the options are their own keys (`Settings.ThemePicker`, `Settings.ThemeOptions`): the setup wizard mounts them too, so the page asks the registry
// for them instead of calling them.

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
    static Element[] AppearanceTab(ZuneKit k)
    {
        var facts = SettingsFacts.Live;
        var groups = new List<Element>(8)
        {
            Group(k, Tab.Appearance, "Theme", Slots.View(ViewKeys.SettingsThemePicker, SettingsThemePickerProps.Instance) with { Key = "zs:picker-view" }),
            Slots.View(ViewKeys.SettingsThemeOptions, SettingsThemeOptionsProps.Page) with { Key = "zs:options-view" },
        };

        var mode = D(Tab.Appearance, "appMode");
        var marquee = D(Tab.Appearance, "marquee");
        groups.Add(Group(k, Tab.Appearance, "Display",
            k.DocRow(mode, k.Choice(mode, ModeLabels(), chips: true)),
            k.DocRow(D(Tab.Appearance, "zoom"), Embed.Comp(static () => new ZoomCell()) with { Key = "zs:zoom" }),
            k.DocRow(marquee, k.Toggle(marquee))));

        var blur = D(Tab.Appearance, "lyricsBlur");
        groups.Add(Group(k, Tab.Appearance, "Lyrics",
            k.DocRow(blur, k.Slide("lyricsBlur", new ZuneSlide(0f, 100f, 1f,
                () => blur.Current is float v ? v : 0f,
                v => { if (blur.Writer.Slide!(v)) Settings.Bump(); },
                static v => ((int)MathF.Round(v)).ToString(CultureInfo.InvariantCulture) + "%"))),
            Embed.Comp(static () => new AiLyricsRow()) with { Key = "zs:ai" }));

        var visualizer = D(Tab.Appearance, "stageVisualizer");
        var layout = D(Tab.Appearance, "stageLayout");
        var sensitivity = D(Tab.Appearance, "stageSensitivity");
        var offset = D(Tab.Appearance, "stageSyncOffset");
        groups.Add(Group(k, Tab.Appearance, "Fullscreen",
            k.DocRow(visualizer, k.Choice(visualizer, VisualizerLabels(), chips: false, width: Z.WideComboWidth)),
            k.DocRow(layout, k.Choice(layout,
            [
                Loc.Get(Strings.Stage.Layout.Card), Loc.Get(Strings.Stage.Layout.Large), Loc.Get(Strings.Stage.Layout.Centered), Loc.Get(Strings.Stage.Layout.Artist),
            ], chips: true)),
            k.DocRow(sensitivity, k.Slide("stageSensitivity", new ZuneSlide(Z.SensitivityMin, Z.SensitivityMax, Z.SensitivityStep,
                () => sensitivity.Current is float v ? v : 1f,
                v => sensitivity.Writer.Slide!(v),
                static v => ((int)MathF.Round(v * 100f)).ToString(CultureInfo.InvariantCulture) + "%"))),
            ToggleRow(k, Tab.Appearance, "stageLyricsOverlay"),
            k.DocRow(offset, k.Slide("stageSyncOffset", new ZuneSlide(Z.SyncOffsetMin, Z.SyncOffsetMax, Z.SyncOffsetStep,
                () => offset.Current is float v ? v : 0f,
                v => offset.Writer.Slide!(v),
                static v => ((int)MathF.Round(v)).ToString(CultureInfo.InvariantCulture) + " ms"))),
            ToggleRow(k, Tab.Appearance, "stageCalm"), ToggleRow(k, Tab.Appearance, "stageMoments")));

        // DEVELOPER-ONLY, the whole group: the Cover / Player switch exists only in developer mode, and the player style is hidden until it affects Now playing.
        var npvStyle = D(Tab.Appearance, "npvStyle");
        if (npvStyle.Visible(facts))
        {
            var presentation = D(Tab.Appearance, "npvPresentation");
            var preset = Rail.PlayerPrefs.CurrentPreset();
            var presets = Rail.PlayerCatalog.Presets;
            var styleLabels = new string[presets.Length];
            for (int i = 0; i < styleLabels.Length; i++) styleLabels[i] = Loc.Get(presets[i].LabelKey);
            groups.Add(Group(k, Tab.Appearance, "Now playing",
                Shown(presentation)
                    ? k.DocRow(presentation, k.Choice(presentation, [Loc.Get(Strings.Player.PresentationCover), Loc.Get(preset.ShortLabelKey)], chips: true))
                    : null,
                k.DocRow(npvStyle, k.Choice(npvStyle, styleLabels, chips: false, width: Z.WideComboWidth))));
        }

        var daylist = D(Tab.Appearance, "daylistClock");
        groups.Add(Group(k, Tab.Appearance, "Home",
            k.DocRow(daylist, k.Choice(daylist,
            [
                Loc.Get(Strings.Settings.Appearance.DaylistClockBoth),
                Loc.Get(Strings.Settings.Appearance.DaylistClockUpdateTime),
                Loc.Get(Strings.Settings.Appearance.DaylistClockCountdown),
            ], chips: true))));
        return groups.ToArray();
    }

    static string[] ModeLabels() =>
    [
        Loc.Get(Strings.Settings.Choice.System),
        Loc.Get(Strings.Settings.Choice.Light),
        Loc.Get(Strings.Settings.Choice.Dark),
    ];

    /// <summary>The shown faces' names in the gallery's order (the combo's items).</summary>
    static string[] VisualizerLabels()
    {
        var shown = Visualizer.Catalog.Shown;
        var labels = new string[shown.Length];
        for (int i = 0; i < labels.Length; i++) labels[i] = Loc.Get(Visualizer.NameKey(shown[i]));
        return labels;
    }

    /// <summary>The Zoom row's combo. Index 0 is "Auto (N%)" with the LIVE resolved percent and is the selection for BOTH Auto and Dense; only Manual selects a rung.
    /// Its own component because it needs the live zoom, the viewport and one effect: a chord or the Ctrl+wheel moves the selection with no settings write.</summary>
    sealed class ZoomCell : ZuneScreenComponent
    {
        readonly Signal<int> _index = new(0);
        int _labelsPercent = -1;
        string[] _labels = [];

        public override Element Render()
        {
            var k = new ZuneKit(UseLayout());
            float zoom = UseContext(Viewport.Zoom);
            var viewport = UseContext(Viewport.Size);
            // baseDip = the live (already zoomed) viewport x zoom: ZoomAutoPolicy takes BASE dips, never live ones.
            float auto = ZoomService.AutoSuggestion(viewport.Width, viewport.Height, zoom);
            SettingsFacts.PublishAutoZoom(auto);   // what the head item ("Auto (N%)") applies when picked
            int index = ZoomService.Mode == ZoomAutoMode.Manual
                ? 1 + Math.Max(0, Array.IndexOf(ZoomLadder.Steps, ZoomLadder.Snap(zoom)))
                : 0;
            UseEffect(() => _index.SetIfChanged(index), DepKey.From(index));

            int percent = ZoomLadder.Percent(auto);
            if (percent != _labelsPercent)
            {
                _labelsPercent = percent;
                _labels = new string[ZoomLadder.Steps.Length + 1];
                _labels[0] = Strings.Settings.Appearance.ZoomAuto(percent);
                for (int i = 0; i < ZoomLadder.Steps.Length; i++)
                    _labels[i + 1] = ZoomLadder.Percent(ZoomLadder.Steps[i]).ToString(CultureInfo.InvariantCulture) + "%";
            }
            // The combo's ITEMS freeze at mount: the head label changes only on a plateau change, so re-key on that.
            return k.Combo(_labels, _index, Z.ComboWidth, true, static i =>
            {
                if (D(Tab.Appearance, "zoom").Writer.Choose!(i)) Settings.Bump();
            }) with { Key = "zs:zoom:" + percent.ToString(CultureInfo.InvariantCulture) };
        }
    }
}
