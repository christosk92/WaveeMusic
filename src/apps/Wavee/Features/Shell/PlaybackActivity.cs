using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Hooks;

namespace Wavee;

/// <summary>A short load does not flash activity. Mount lifetime owns the delay; withdrawing play intent unmounts it.</summary>
sealed class PlaybackActivity : Component
{
    public required float Width;

    public override Element Render()
    {
        var visible = UseSignal(false);
        UseTimeout(() => visible.Value = true, MotionTok.ControlFast.DurationMs, DepKey.Empty);
        return visible.Value ? ProgressBar.Indeterminate(Width)
            : new BoxEl { Width = Width, Height = 1f, HitTestVisible = false };
    }
}
