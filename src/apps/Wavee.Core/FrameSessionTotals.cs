using System;

namespace Wavee.Core;

/// <summary>Allocation-free counters for every completed UI frame, independent of navigation/scroll windows.</summary>
public struct FrameSessionTotals
{
    public long Frames { get; private set; }
    public long Over83 { get; private set; }
    public long OverRefresh { get; private set; }
    public long Invalid { get; private set; }
    public double WorstMs { get; private set; }

    public void Add(double frameMs, double refreshMs)
    {
        Frames++;
        if (!double.IsFinite(frameMs) || frameMs < 0)
        {
            Invalid++;
            return;
        }
        if (frameMs > 8.3) Over83++;
        if (double.IsFinite(refreshMs) && refreshMs > 0)
        {
            if (frameMs > refreshMs) OverRefresh++;
        }
        else Invalid++;
        WorstMs = Math.Max(WorstMs, frameMs);
    }
}
