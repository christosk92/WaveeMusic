using System;

namespace Wavee.Backend.Audio;

/// <summary>Pure (COM-free) helper for the two-way Windows session-volume sync (plan §B1). The WASAPI session sink calls
/// <see cref="TryReadExternal"/> to decide whether a volume event is an EXTERNAL change (SndVol / another app) worth
/// reflecting, or a self-originated echo of our own set (matched by the per-process event-context GUID). Unit-tested
/// directly (SessionVolumeSyncTests) so the sink logic needs no COM.</summary>
public static class SessionVolumeSync
{
    /// <summary>Returns false (suppress) when <paramref name="eventContext"/> equals our <paramref name="ownContext"/>
    /// (WASAPI echoes our own sets back with our context). Otherwise yields the inverse-tapered slider position
    /// (<c>cbrt(scalar)</c>) and the mute flag.</summary>
    public static bool TryReadExternal(Guid eventContext, Guid ownContext, float scalar, int mute, out double slider, out bool muted)
    {
        if (eventContext == ownContext) { slider = 0; muted = false; return false; }
        slider = VolumeTaper.Slider(Math.Clamp(scalar, 0f, 1f));
        muted = mute != 0;
        return true;
    }
}
