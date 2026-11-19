using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace Wavee.Theming;

/// <summary>Every volume rail (the bars, the full screen, the overlays, the device pickers): the stock slider's thumb and input over a rail of its
/// own, with the wheel.
/// <para>THE ENDS ARE THE ENDS. The stock slider's rail spans its whole length and its thumb stops flush with it, so at 0 % (and 100 %) the
/// thumb's centre stood half a thumb inside the rail and a silent rail read as a little volume; below about 12 % the thumb did not move at
/// all. Here the rail runs between the two half-thumb insets and the thumb's CENTRE travels it linearly: 0 % sits on the rail's start, 100 %
/// on its end. The stock rail and fill are hidden (template parts) and drawn underneath instead: the rail at the inset, the fill from the
/// rail's start to the thumb's centre (gone outright at 0: a shrunk fill rasterised as a 1 % sliver). The stock slider is handed a range
/// stretched by the inset (<see cref="Insets"/>), so its thumb and its pointer mapping land on the same centre line; a press or a key
/// past an end clamps to 0 or 1.</para>
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
    /// <summary>At or below this the fill is hidden (the level reads as 0).</summary>
    const float ZeroLevel = .0005f;

    public static Element Create(FloatSignal value, Action<float> setVolume, Slider.SliderOptions options,
                                 float length, float thickness, Slider.Style? style = null)
        => Embed.Comp(new RailArgs(value, setVolume, options, length, thickness, style), static () => new Rail());

    sealed record RailArgs(FloatSignal Value, Action<float> SetVolume, Slider.SliderOptions Options, float Length, float Thickness,
                           Slider.Style? Style);

    /// <summary>The stock slider's range for a rail inset by half a thumb at each end: 0..1 on the rail is <c>-h/W .. 1 + h/W</c> on the slider
    /// (<c>h</c> half the thumb, <c>W</c> the rail between the insets), which puts the stock thumb's centre at <c>h + t·W</c> and maps a press at
    /// <c>x</c> to <c>(x − h) / W</c>, both orientations.</summary>
    static (float Min, float Max) Insets(float half, float rail) => (-half / rail, 1f + half / rail);

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
        TemplateParts? _parts;

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
            // The stock rail and fill are drawn here instead (below), inset.
            _parts ??= new TemplateParts
            {
                [Slider.PartRail] = static rail => rail with { Opacity = 0f },
                [Slider.PartValueFill] = static fill => fill with { Opacity = 0f },
            };

            var style = a.Style ?? Slider.DefaultStyle;
            bool vertical = a.Options.Vertical;
            float length = a.Length, thickness = a.Thickness;
            float half = style.ThumbRingDiameter * 0.5f, track = style.TrackHeight;
            float rail = MathF.Max(1f, length - 2f * half);
            var (min, max) = Insets(half, rail);
            var conv = a.Options.ThumbToolTipValueConverter;
            var options = a.Options with
            {
                Min = min, Max = max,
                SmallChange = a.Options.SmallChange > 0f ? a.Options.SmallChange : .01f,
                LargeChange = a.Options.LargeChange > 0f ? a.Options.LargeChange : .1f,
                ThumbToolTipValueConverter = conv is null ? null : v => conv(Math.Clamp(v, 0f, 1f)),
            };

            var corners = CornerRadius4.All(style.TrackCornerRadius);
            float cross = (thickness - track) * 0.5f;
            // The fill grows from the rail's start (the bottom, vertical) to the thumb's centre: a scale about its centre, then a slide.
            Func<Affine2D> fillBind = vertical
                ? () => { float t = Math.Clamp(shown.Value, 0f, 1f); return Affine2D.Translation(0f, rail * (1f - t) * 0.5f).Multiply(Affine2D.Scale(1f, MathF.Max(t, 1e-4f))); }
                : () => { float t = Math.Clamp(shown.Value, 0f, 1f); return Affine2D.Translation(-rail * (1f - t) * 0.5f, 0f).Multiply(Affine2D.Scale(MathF.Max(t, 1e-4f), 1f)); };
            var railBox = new BoxEl
            {
                Width = vertical ? track : rail, Height = vertical ? rail : track, Corners = corners,
                Margin = vertical ? new Edges4(cross, half, 0f, 0f) : new Edges4(half, cross, 0f, 0f),
                Fill = Prop.Of(() => (_a!.Style ?? Slider.DefaultStyle).RailFill),
            };
            var fillBox = new BoxEl
            {
                Width = vertical ? track : rail, Height = vertical ? rail : track, Corners = corners,
                Margin = vertical ? new Edges4(cross, half, 0f, 0f) : new Edges4(half, cross, 0f, 0f),
                Fill = Prop.Of(() => (_a!.Style ?? Slider.DefaultStyle).ValueFill),
                Transform = fillBind,
                Opacity = Prop.Of(() => shown.Value > ZeroLevel ? 1f : 0f),
            };

            return new BoxEl
            {
                ZStack = true,
                OnPointerWheel = e =>
                {
                    e.Handled = true;
                    if (e.Steps == 0) return;
                    float v = Math.Clamp(shown.Peek() - WheelStep * e.Steps, 0f, 1f);
                    if (v == shown.Peek()) return;
                    shown.Value = v;
                    Set(v);
                },
                Children =
                [
                    new BoxEl
                    {
                        ZStack = true, HitTestVisible = false,
                        Width = vertical ? thickness : length, Height = vertical ? length : thickness,
                        Children = [railBox, fillBox],
                    },
                    Slider.Create(shown, _set, options, length, thickness, a.Style, parts: _parts),
                ],
            };
        }

        /// <summary>The rail's own write (the slider already moved the thumb): a press past an end clamps, a glide stops, the model hears it.</summary>
        void Set(float v)
        {
            float level = Math.Clamp(v, 0f, 1f);
            if (level != v) _shown!.Value = level;
            _running = false;
            _ownAt = _timer.NowMs;
            _a!.SetVolume(level);
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
