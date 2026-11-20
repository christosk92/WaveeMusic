using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace Wavee.Theming;

/// <summary>Every volume rail (the bars, the full screen, the overlays, the device pickers): the stock slider with the wheel and the glide.
/// The stock slider puts 0 % on its rail's start and 100 % on its end, the thumb's centre travelling the rail linearly, and draws no fill at
/// 0 (fluent-gpu's <c>Slider</c>), so a silent rail reads as silent.
/// <para>Every caller feeds it the HEARD level (<c>Playback.HeardVolume</c>: 0 while muted), so a mute takes every rail to 0.</para>
/// <para>THE GLIDE. The rail draws its own shown level. A change that comes from OUTSIDE it (a mute or an unmute, the volume keys, another
/// device moving the level) moves the thumb there over the Slow beat, eased in and out (a heavy ease-out read as a snap, and FluentStandard's
/// steep middle as a jump), on the host's timer queue: one timer re-armed for the NEXT frame while it moves (the queue defers a re-arm made
/// during its drain to the next drain, so a 0 ms re-arm is exactly one step per frame and can never spin), no re-render. The rail's own
/// writes (a drag, a press, the wheel) land at once, and so does any change inside <see cref="OwnQuietMs"/> of one (the level the model
/// publishes back trails a drag by a drain), so the thumb never fights the pointer. A step smaller than <see cref="GlideMinStep"/> and
/// reduced motion land at once too.</para></summary>
public static class VolumeSlider
{
    public const float WheelStep = .05f;

    /// <summary>How long after the rail's own write an outside change still lands at once.</summary>
    const double OwnQuietMs = 300;
    /// <summary>An outside change at least this big glides (the volume keys' 5 % step does; sub-percent echoes do not).</summary>
    const float GlideMinStep = .02f;

    public static Element Create(FloatSignal value, Action<float> setVolume, Slider.SliderOptions options,
                                 float length, float thickness, Slider.Style? style = null)
        => Embed.Comp(new RailArgs(value, setVolume, options, length, thickness, style), static () => new Rail());

    sealed record RailArgs(FloatSignal Value, Action<float> SetVolume, Slider.SliderOptions Options, float Length, float Thickness,
                           Slider.Style? Style);

    sealed class Rail : Component
    {
        RailArgs? _a;
        FloatSignal? _shown;
        TimerHandle _timer;
        double _ownAt = double.NegativeInfinity, _start;
        float _from, _to;
        bool _running;
        Action? _tick;
        Action<float>? _set;

        public override Element Render()
        {
            var a = UseProps<RailArgs>();
            _a = a;
            var shown = UseFloatSignal(a.Value.Peek());
            _shown = shown;
            _timer = UseTimeout(_tick ??= Step, 0f);
            // Reads the CURRENT args' level each run (a re-pushed signal is followed from its next change).
            UseSignalEffect(() => Follow(_a!.Value.Value));
            _set ??= Set;

            return new BoxEl
            {
                OnPointerWheel = e =>
                {
                    e.Handled = true;
                    if (e.Steps == 0) return;
                    float v = Math.Clamp(shown.Peek() - WheelStep * e.Steps, 0f, 1f);
                    if (v == shown.Peek()) return;
                    shown.Value = v;
                    Set(v);
                },
                Children = [Slider.Create(shown, _set, a.Options, a.Length, a.Thickness, a.Style)],
            };
        }

        /// <summary>The rail's own write (the slider already moved the thumb): a glide stops, the model hears it.</summary>
        void Set(float v)
        {
            _running = false;
            _ownAt = _timer.NowMs;
            _a!.SetVolume(v);
        }

        /// <summary>The level moved outside the rail: glide there, or land.</summary>
        void Follow(float level)
        {
            var shown = _shown!;
            if (_running && level == _to) return;
            float current = shown.Peek();
            double now = _timer.NowMs;
            if (now - _ownAt < OwnQuietMs || Design.Reduced || MathF.Abs(level - current) < GlideMinStep)
            {
                _running = false;
                shown.SetIfChanged(level);
                return;
            }
            _from = current;
            _to = level;
            _start = now;
            _running = true;
            _timer.RestartIn(0f);
        }

        void Step()
        {
            if (!_running) return;
            float t = Math.Clamp((float)(_timer.NowMs - _start) / Design.Motion.Slow, 0f, 1f);
            _shown!.SetIfChanged(_from + (_to - _from) * Easings.Ease(Easing.EaseInOut, t));
            if (t < 1f) _timer.RestartIn(0f);
            else _running = false;
        }
    }
}
