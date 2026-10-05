// ── Platform/Residency.Heap.cs — the managed heap's share of the governor: when to hand dead large objects back ────
//
// The arenas (`Residency.Pins.cs`) shed what the app holds. This sheds what nobody holds any more and the GC has not
// collected: the owner's long sessions sat at a 220-390 MB large-object heap with up to 130 MB of it free space, and the
// governor's Critical trim logged `freed=0B` every 30 s, because nothing it could shed was what filled the process.
//
// WHY THE GC DOES NOT DO THIS ITSELF. A large object dies on the LOH and stays there until a gen2 collection; the free
// space between survivors stays committed until a COMPACTING one. `App.cs` runs the process in SustainedLowLatency, which
// keeps the GC from choosing a blocking gen2 on its own, so in practice the LOH is never compacted, and an idle app that
// allocates little runs no gen2 at all. SustainedLowLatency stays: the audio feed thread is managed and the device FIFO
// is ~100 ms, so a blocking full collection the GC picks while music plays is an audible dropout. Instead the governor
// picks the moments itself, on its 30 s poll:
//   BACKGROUND  the heap grew a lot since the last full GC → one background (concurrent) gen2. It runs beside the app
//               with only short suspensions, so it is safe while music plays and the window is in use; it turns the
//               dead large objects into reusable free space and returns whole empty regions.
//   COMPACT     nothing is playing AND nobody is looking (window minimized/hidden, or no input for minutes) → one
//               blocking, LOH-compacting, decommitting collection: the free space between survivors goes back too.
// The decision is pure (`HeapPolicy`, unit-tested at its boundaries); `Residency.HeapTick` only reads the facts and acts.
// UI thread (the governor's poll), like the rest of the governor.

using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;

using FluentGpu;

namespace Wavee;

/// <summary>What the governor does with the managed heap on one poll.</summary>
public enum HeapAction : byte
{
    None,
    /// <summary>One background (concurrent) gen2 collection: dead large objects become free space, empty regions go
    /// back to the OS. Short suspensions only — safe while audio plays.</summary>
    Background,
    /// <summary>One blocking collection that compacts the large-object heap and decommits what it frees. Only when
    /// nothing is audible and nobody is looking.</summary>
    Compact,
}

/// <summary>The facts one heap decision reads. Byte counts are <see cref="GCMemoryInfo"/>'s; times are milliseconds.</summary>
/// <param name="HeapBytes">The managed heap now (<c>GCMemoryInfo.HeapSizeBytes</c> of the last GC).</param>
/// <param name="FragmentedBytes">Free space inside the heap (<c>GCMemoryInfo.FragmentedBytes</c>) — what only a compaction
/// returns.</param>
/// <param name="BaselineBytes">The heap right after the last FULL collection (blocking or background): what was live
/// then. <c>HeapBytes - BaselineBytes</c> is what has piled up since.</param>
/// <param name="Audible">Something is playing.</param>
/// <param name="OnScreen">The window is shown and not minimized.</param>
/// <param name="IdleMs">Since the last keyboard/mouse input anywhere in the session.</param>
/// <param name="MsSinceCompact">Since this governor last compacted (long.MaxValue = never).</param>
/// <param name="MsSinceBackground">Since this governor last asked for a background collection (long.MaxValue = never).</param>
/// <param name="Pressure">The poll's pressure level.</param>
public readonly record struct HeapFacts(
    long HeapBytes, long FragmentedBytes, long BaselineBytes, bool Audible, bool OnScreen, long IdleMs,
    long MsSinceCompact, long MsSinceBackground, MemoryPressure Pressure);

/// <summary>THE heap decision, pure: no clock, no GC API. See the file header for the reasoning.</summary>
public static class HeapPolicy
{
    /// <summary>Growth since the last full GC that is worth a background gen2 at Normal pressure. Halved at Moderate
    /// and quartered at Critical: the more the process weighs, the sooner its dead objects should go.</summary>
    public const long BackgroundGrowthBytes = 96L << 20;

    /// <summary>Free space (or growth) that is worth a blocking compaction when one is allowed at all.</summary>
    public const long CompactBytes = 32L << 20;

    /// <summary>No input for this long counts as "nobody is looking" even with the window on screen.</summary>
    public const long IdleMsForCompact = 5 * 60_000;

    /// <summary>At most one compaction per this interval: a parked app that keeps allocating (a lyrics job, a sync)
    /// must not compact every poll.</summary>
    public const long MinMsBetweenCompacts = 15 * 60_000;

    /// <summary>At most one requested background collection per this interval.</summary>
    public const long MinMsBetweenBackground = 2 * 60_000;

    public static long BackgroundThreshold(MemoryPressure pressure) => pressure switch
    {
        MemoryPressure.Critical => BackgroundGrowthBytes / 4,
        MemoryPressure.Moderate => BackgroundGrowthBytes / 2,
        _ => BackgroundGrowthBytes,
    };

    /// <summary>Is a blocking collection allowed right now? Never while audible (the managed feed thread would stall
    /// past the device FIFO); otherwise only when the window is parked or the user has been away for minutes.</summary>
    public static bool MayBlock(bool audible, bool onScreen, long idleMs) => !audible && (!onScreen || idleMs >= IdleMsForCompact);

    public static HeapAction Decide(in HeapFacts f)
    {
        long grown = Math.Max(0, f.HeapBytes - f.BaselineBytes);
        if (MayBlock(f.Audible, f.OnScreen, f.IdleMs) && f.MsSinceCompact >= MinMsBetweenCompacts
            && (f.FragmentedBytes >= CompactBytes || grown >= CompactBytes))
            return HeapAction.Compact;
        if (grown >= BackgroundThreshold(f.Pressure) && f.MsSinceBackground >= MinMsBetweenBackground)
            return HeapAction.Background;
        return HeapAction.None;
    }
}

public static partial class Residency
{
    static long s_lastCompactMs = long.MinValue, s_lastBackgroundMs = long.MinValue;

    /// <summary>One heap decision and its action (see the file header). Called by <see cref="Poll"/> on the UI thread.
    /// Returns the committed bytes a compaction handed back (0 for a background request: that one finishes on the GC's
    /// own thread, and the next poll's numbers show it).</summary>
    static long HeapTick(MemoryPressure level, out HeapAction action)
    {
        long now = Environment.TickCount64;
        GCMemoryInfo info = GC.GetGCMemoryInfo();
        var facts = new HeapFacts(
            HeapBytes: info.HeapSizeBytes,
            FragmentedBytes: info.FragmentedBytes,
            BaselineBytes: LastFullGcHeapBytes(),
            Audible: Playback.Snap().IsPlaying,
            OnScreen: WindowOnScreen(),
            IdleMs: MsSinceInput(),
            MsSinceCompact: s_lastCompactMs == long.MinValue ? long.MaxValue : now - s_lastCompactMs,
            MsSinceBackground: s_lastBackgroundMs == long.MinValue ? long.MaxValue : now - s_lastBackgroundMs,
            Pressure: level);
        action = HeapPolicy.Decide(in facts);
        switch (action)
        {
            case HeapAction.Background:
                s_lastBackgroundMs = now;
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: false, compacting: false);
                return 0;
            case HeapAction.Compact:
            {
                s_lastCompactMs = now;
                s_lastBackgroundMs = now;
                long committed = info.TotalCommittedBytes;
                long pause = GC.GetTotalPauseDuration().Ticks;
                var sw = Stopwatch.StartNew();
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                GCMemoryInfo after = GC.GetGCMemoryInfo();
                Log.Info("memory", $"heap.compact heap={info.HeapSizeBytes >> 20}->{after.HeapSizeBytes >> 20}MB committed={committed >> 20}->{after.TotalCommittedBytes >> 20}MB "
                    + $"fragmented={info.FragmentedBytes >> 20}MB pauseMs={TimeSpan.FromTicks(GC.GetTotalPauseDuration().Ticks - pause).TotalMilliseconds:0} wallMs={sw.ElapsedMilliseconds}");
                return Math.Max(0, committed - after.TotalCommittedBytes);
            }
            default:
                return 0;
        }
    }

    /// <summary>The heap size the most recent full collection left behind — blocking or background, whoever ran it.
    /// 0 before the first one (everything since launch counts as growth).</summary>
    static long LastFullGcHeapBytes()
    {
        GCMemoryInfo blocking = GC.GetGCMemoryInfo(GCKind.FullBlocking), background = GC.GetGCMemoryInfo(GCKind.Background);
        GCMemoryInfo last = background.Index > blocking.Index ? background : blocking;
        return last.Index == 0 ? 0 : last.HeapSizeBytes;
    }

    static bool WindowOnScreen()
    {
        nint hwnd = FluentApp.WindowHandle;
        return FluentApp.WindowVisible && hwnd != 0 && !IsIconic(hwnd);
    }

    /// <summary>Milliseconds since the last input event in this session (GetLastInputInfo), or 0 when unknown — "the
    /// user is here" is the safe reading.</summary>
    static long MsSinceInput()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>(), dwTime = 0 };
        if (!GetLastInputInfo(ref info)) return 0;
        return Math.Max(0, (long)unchecked((uint)Environment.TickCount - info.dwTime));
    }

    [StructLayout(LayoutKind.Sequential)]
    struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLastInputInfo(ref LASTINPUTINFO info);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint hwnd);
}
