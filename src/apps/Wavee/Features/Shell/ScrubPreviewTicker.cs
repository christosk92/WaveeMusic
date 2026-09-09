using FluentGpu.Dsl;
using FluentGpu.Hooks;

namespace Wavee;

/// <summary>Mounted only during a gesture so the latest stationary pointer target is delivered when its throttle opens.</summary>
sealed class ScrubPreviewTicker : Component
{
    public required SeekBar Owner;

    public override Element Render()
    {
        UseInterval(Owner.DrainPreview, FluentGpu.Media.SeekPreviewScheduler.IntervalMs);
        return new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false };
    }
}
