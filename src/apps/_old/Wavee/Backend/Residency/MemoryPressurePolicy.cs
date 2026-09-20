using System;

namespace Wavee.Backend.Residency;

/// <summary>
/// Turns a memory reading into a <see cref="MemoryPressure"/> level. Pure — no clock, no GC API, no globals — so the
/// decision can be unit-tested at every boundary instead of inferred from a running app.
/// <para><b>Why this exists.</b> The level used to come from <c>GC.GetGCMemoryInfo()</c>'s
/// <c>MemoryLoadBytes / HighMemoryLoadThresholdBytes</c>, which is WHOLE-MACHINE load against the CLR's
/// high-memory threshold (~90 % of physical). On a 16–32 GB laptop that ratio sits near zero while this process holds
/// a gigabyte, so the governor could never leave Normal no matter how much Wavee itself was using — it was keyed to a
/// signal the app cannot move. Pressure is now read from the process's OWN footprint, which is the thing shedding an
/// arena actually changes.</para>
/// <para>Machine load is still consulted, as a ceiling rather than the primary signal: if the whole machine is under
/// real pressure, this process should shed even when its own footprint looks modest.</para>
/// </summary>
public static class MemoryPressurePolicy
{
    /// <summary>Process private bytes at which the cheapest arenas start shedding.</summary>
    public const long ModerateBytes = 600L * 1024 * 1024;

    /// <summary>Process private bytes at which every arena sheds, including the expensive ones.</summary>
    public const long CriticalBytes = 900L * 1024 * 1024;

    /// <summary>Whole-machine load (0..1 of the CLR's high-memory threshold) that forces the matching level
    /// regardless of this process's own size.</summary>
    public const double MachineModerateLoad = 0.85;
    public const double MachineCriticalLoad = 1.0;

    /// <param name="processPrivateBytes">This process's private bytes — what shedding actually reduces.</param>
    /// <param name="machineLoad">Whole-machine memory load as a fraction of the CLR's high-memory threshold, or 0
    /// when it cannot be determined. Only ever RAISES the level.</param>
    public static MemoryPressure From(long processPrivateBytes, double machineLoad)
    {
        if (processPrivateBytes >= CriticalBytes || machineLoad >= MachineCriticalLoad) return MemoryPressure.Critical;
        if (processPrivateBytes >= ModerateBytes || machineLoad >= MachineModerateLoad) return MemoryPressure.Moderate;
        return MemoryPressure.Normal;
    }
}
