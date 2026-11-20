// ── Themes/Zune/Structure/Views/Screens/ZuneSettings.Appearance.cs — Appearance's sections: the theme picker and the Zoom cell (settings rework phase 4) ─
//
// Board Z-Settings-Appearance. The picker is its own key (`Settings.ThemePicker`, `ZuneSettings.Theme.cs`): the setup wizard mounts it too, so the page asks the
// registry for it, under the Theme group's heading at the column's full width. The active theme's options are the body's options group (drawn by the body, each
// option's control from `ZuneSettings.Theme.cs`); App mode, Marquee, Fast navigation and the Home and library dials are generic rows.

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
using Z = Wavee.Themes.Zune.ZuneSettingsRules;

namespace Wavee.Themes.Zune;

internal static partial class ZuneSettings
{
    /// <summary>The theme picker at the Theme group's full width (the group's heading names it), keyed as the theme dial's row.</summary>
    static Element PickerBlock(SettingsBodyRow row) => new BoxEl
    {
        Key = SettingsKeys.ZuneRow(row.Id), Direction = 1, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, OnRealized = NoteRow(row.Id),
        Children = [Slots.View(ViewKeys.SettingsThemePicker, SettingsThemePickerProps.Instance) with { Key = "zs:picker-view" }],
    };

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
                if (SettingsSpec.Dial(ZoomDial).Writer.Choose?.Invoke(i) == true) Settings.Bump();
            }) with { Key = "zs:zoom:" + percent.ToString(CultureInfo.InvariantCulture) };
        }
    }

    const string ZoomDial = "appearance.zoom";
}
