using System;

namespace Wavee;

/// <summary>Route-owned reveal accounting, independent of the renderer and logging.</summary>
internal sealed class PageRevealDecision(string route, string? arg)
{
    long _navigationId;
    bool _emitted;
    public bool RetainedUi { get; private set; }
    public bool ReadyAtFirstFrame { get; private set; }

    public bool Observe(long navigationId, string currentRoute, string? currentArg,
        bool active, bool rendered, bool readyContentIncluded, bool currentlyReady)
    {
        if (navigationId <= 0 || !active || !rendered
            || !string.Equals(route, currentRoute, StringComparison.Ordinal)
            || !string.Equals(arg ?? "", currentArg ?? "", StringComparison.Ordinal)) return false;

        if (_navigationId != navigationId)
        {
            RetainedUi = _navigationId != 0;
            ReadyAtFirstFrame = readyContentIncluded && currentlyReady;
            _navigationId = navigationId;
            _emitted = false;
        }
        if (_emitted || !readyContentIncluded || !currentlyReady) return false;
        _emitted = true;
        return true;
    }
}
