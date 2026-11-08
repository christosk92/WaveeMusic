using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Signals;

namespace Wavee.Theming;

public static class VolumeSlider
{
    public const float WheelStep = .05f;

    public static Element Create(FloatSignal value, Action<float> setVolume, Slider.SliderOptions options,
                                 float length, float thickness, Slider.Style? style = null)
        => new BoxEl
        {
            Direction = 1,
            OnPointerWheel = e =>
            {
                e.Handled = true;
                if (e.Steps == 0) return;
                float v = Math.Clamp(value.Peek() - WheelStep * e.Steps, 0f, 1f);
                if (v == value.Peek()) return;
                value.Value = v;
                setVolume(v);
            },
            Children = [Slider.Create(value, setVolume, options, length, thickness, style)],
        };
}
