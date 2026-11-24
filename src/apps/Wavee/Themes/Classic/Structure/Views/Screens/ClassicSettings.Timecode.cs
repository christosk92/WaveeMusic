// ── Themes/Classic/Structure/Views/Screens/ClassicSettings.Timecode.cs — Sound › Timecode's two sliders (#154) ──────────────────
//
// The timecode group draws through Classic's generic grammar (the switch is an expander, its children items) except the level and
// the offset: the registry carries no range, so each slider names its own here. Like the other Classic sliders, a tiny child
// component keeps ONE float signal in step with the dial (a Slider freezes its signal at mount) and writes through the dial's writer.

using System.Globalization;

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

internal static partial class ClassicSettings
{
    static readonly Slider.SliderOptions s_timecodeLevelOptions = new()
    {
        Min = -24f, Max = 0f, Step = 1f, TickFrequency = 6f, IsThumbToolTipEnabled = true,
        ThumbToolTipValueConverter = static v => ((int)MathF.Round(v)).ToString(CultureInfo.InvariantCulture) + " dB",
    };
    static readonly Slider.SliderOptions s_timecodeOffsetOptions = new()
    {
        Min = -500f, Max = 500f, Step = 5f, TickFrequency = 250f, IsThumbToolTipEnabled = true,
        ThumbToolTipValueConverter = static v => ((int)MathF.Round(v)).ToString(CultureInfo.InvariantCulture) + " ms",
    };

    /// <summary>The level or offset row as an expander item: its label and description, the slider, greyed with the switch.</summary>
    static Element TimecodeSliderItem(SettingsBodyRow row, Slider.SliderOptions options)
        => Item(row.Label, row.Description,
            Badged(row, Embed.Comp(new TimecodeSliderProps(row.Id, row.Value is float f ? f : options.Min, options, row.Enabled),
                static () => new TimecodeSlider()) with { Key = "slider:" + row.Id }),
            isEnabled: row.Enabled, icon: GlyphOf(row.Dial));

    sealed record TimecodeSliderProps(string DialId, float Value, Slider.SliderOptions Options, bool Enabled);

    sealed class TimecodeSlider : SettingsComponent
    {
        public override Element Render()
        {
            UseLayout();
            var p = UseProps<TimecodeSliderProps>();
            var value = UseFloatSignal(p.Value);
            float live = p.Value;
            UseEffect(() => value.SetIfChanged(live), DepKey.From(live));
            string id = p.DialId;
            return SliderControl(value, v => Dial(id).Writer.Slide!(v), p.Options, length: ClassicSettingsRules.SliderLength, isEnabled: p.Enabled);
        }
    }
}
