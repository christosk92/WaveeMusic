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
// is ~100 ms, so a blocking full collection while a track plays or opens is an audible dropout or a late start. Instead
// the governor picks the moments itself, on its 30 s poll:
//   BACKGROUND  the heap grew a lot since the last full GC → one background (concurrent) gen2. It runs beside the app
//               with only short suspensions; it turns the dead large objects into reusable free space and returns whole
//               empty regions. If the runtime ever runs such a request as a BLOCKING collection instead (the log line
//               says so), the governor stops asking for it while the transport is engaged.
//   COMPACT     the window is minimized or hidden AND the transport is settled — nothing playing, opening or pending,
//               and no phase change for a while — → one blocking, LOH-compacting, decommitting collection: the free space
//               between survivors goes back too. A media key, a Connect transfer or a tray Play can start a track while
//               the window is parked, which is why "not playing right now" is not enough.
//   NAVIGATION  after a page has landed, its enter leg has run (NavGcPolicy.QuietMs) and the app is settled (nothing moving
//               that a person set in motion, no UI animation, nothing pending, no click/key/wheel in the last
//               NavGcPolicy.InteractionQuietMs, no newer navigation in flight) → one gen-0 collection, if gen 0 took at least
//               NavGcPolicy.MinGen0Bytes since the last GC.
//               A gen-0 collection fires when the gen-0 budget runs out (~4.4 MB between natural gen-0s in the nav-gc bench;
//               the runtime caps it at 6 MB), and a page mount (~1-2 MB) is the biggest
//               allocator in a navigation, so it usually ran INSIDE a mount frame (a 3-4 ms pause on the frame the page
//               appears on, about one navigation in three). Collected while the app is idle instead, the next mount starts
//               with an empty budget. At most once per navigation; never while a scroll/drag/press is live, an animation runs
//               or a track is opening. It does NOT cost less here than in a mount frame: a gen-0 pause is paid for what
//               SURVIVES, and what survives is the page just mounted (~1.6 MB promoted, ~4.5 ms p50 in the nav-gc bench);
//               the gain is only WHERE the pause lands. (nav-gc RCA, 2026-10-18: the gate used to be
//               AppHost.IsInteractionQuiet, whose 2 s input hold counts hover moves too, so with a hand on the mouse the
//               collection never ran (0 of 48 navigations); and a page that landed less than QuietMs before the next click
//               was collected 40-100 ms into the NEXT navigation's transition. Now AppHost.IsSettledForPause, and a
//               navigation in flight waits.) Driven by the governor's one-shot settle timer (Shell.Host.cs, ArmNavGc), not by frames:
//               an idle app paints none. (page-mount RCA, round 5: the GC knobs - RegionSize, ConserveMemory - do not move
//               the gen-0 budget or the GC rate; see Wavee.csproj.)
// The decisions are pure (`HeapPolicy`, `NavGcPolicy`, unit-tested at their boundaries); `Residency.HeapTick` and
// `Residency.NavGcTick` only read the facts and act.
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
    /// back to the OS. Short suspensions only.</summary>
    Background,
    /// <summary>One blocking collection that compacts the large-object heap and decommits what it frees. Only when
    /// nobody is looking and the transport is settled.</summary>
    Compact,
}

/// <summary>The facts one heap decision reads. Byte counts are <see cref="GCMemoryInfo"/>'s; times are milliseconds.</summary>
/// <param name="HeapBytes">The managed heap now (<c>GCMemoryInfo.HeapSizeBytes</c> of the last GC).</param>
/// <param name="FragmentedBytes">Free space inside the heap (<c>GCMemoryInfo.FragmentedBytes</c>) — what only a compaction
/// returns.</param>
/// <param name="BaselineBytes">The heap right after the last FULL collection (blocking or background): what was live
/// then. <c>HeapBytes - BaselineBytes</c> is what has piled up since.</param>
/// <param name="TransportEngaged">A track is playing or opening (phase Playing or Loading), or a load is pending.</param>
/// <param name="MsSinceTransportChange">Since the playback phase last changed (long.MaxValue = never).</param>
/// <param name="OnScreen">The window is shown and not minimized.</param>
/// <param name="MsSinceCompact">Since this governor last compacted (long.MaxValue = never).</param>
/// <param name="MsSinceBackground">Since this governor last asked for a background collection (long.MaxValue = never).</param>
/// <param name="Pressure">The poll's pressure level.</param>
/// <param name="BackgroundRanBlocking">A background request of this governor's was once run as a BLOCKING collection by
/// the runtime: from then on it is not requested while the transport is engaged.</param>
public readonly record struct HeapFacts(
    long HeapBytes, long FragmentedBytes, long BaselineBytes, bool TransportEngaged, long MsSinceTransportChange, bool OnScreen,
    long MsSinceCompact, long MsSinceBackground, MemoryPressure Pressure, bool BackgroundRanBlocking = false);

/// <summary>THE heap decision, pure: no clock, no GC API. See the file header for the reasoning.</summary>
public static class HeapPolicy
{
    /// <summary>Growth since the last full GC that is worth a background gen2 at Normal pressure. Halved at Moderate
    /// and quartered at Critical: the more the process weighs, the sooner its dead objects should go.</summary>
    public const long BackgroundGrowthBytes = 96L << 20;

    /// <summary>Free space (or growth) that is worth a blocking compaction when one is allowed at all.</summary>
    public const long CompactBytes = 32L << 20;

    /// <summary>How long the playback phase must have stood still (Idle, Paused or Ended) before a blocking collection:
    /// a track that just ended may be followed by the next one, a pause by a resume.</summary>
    public const long SettledMsForCompact = 60_000;

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

    /// <summary>Is a blocking collection allowed right now? Only with the window parked (minimized or hidden) and the
    /// transport settled: nothing playing, opening or pending, and no phase change for <see cref="SettledMsForCompact"/>.</summary>
    public static bool MayBlock(bool transportEngaged, long msSinceTransportChange, bool onScreen)
        => !onScreen && !transportEngaged && msSinceTransportChange >= SettledMsForCompact;

    public static HeapAction Decide(in HeapFacts f)
    {
        long grown = Math.Max(0, f.HeapBytes - f.BaselineBytes);
        if (MayBlock(f.TransportEngaged, f.MsSinceTransportChange, f.OnScreen) && f.MsSinceCompact >= MinMsBetweenCompacts
            && (f.FragmentedBytes >= CompactBytes || grown >= CompactBytes))
            return HeapAction.Compact;
        if (grown >= BackgroundThreshold(f.Pressure) && f.MsSinceBackground >= MinMsBetweenBackground
            && !(f.BackgroundRanBlocking && f.TransportEngaged))
            return HeapAction.Background;
        return HeapAction.None;
    }
}

/// <summary>What the post-navigation collection does on one settle tick.</summary>
public enum NavGcVerdict : byte
{
    /// <summary>Nothing more for this navigation (none pending, already collected, too little garbage, or it gave up).</summary>
    Done,
    /// <summary>Not yet: too soon after the landing, or not quiet. Check again on the next settle tick.</summary>
    Wait,
    /// <summary>Run one gen-0 collection now.</summary>
    Collect,
}

/// <summary>The facts one post-navigation decision reads.</summary>
/// <param name="Pending">A navigation landed since the last post-navigation collection (or give-up).</param>
/// <param name="MsSinceLanding">Since that landing.</param>
/// <param name="HostQuiet">The engine host is settled (<c>AppHost.IsSettledForPause</c>): nothing a person set in motion is
/// moving, no UI animation, no reactive work pending, no click/key/wheel in the last <see cref="NavGcPolicy.InteractionQuietMs"/>.</param>
/// <param name="TransportCritical">A track is opening or a load is pending (a pause there is a late start).</param>
/// <param name="Gen0Bytes">Allocated since the last gen-0 collection the governor saw (an under-estimate, never an over-).</param>
/// <param name="NavigationInFlight">A newer navigation was committed and has not landed yet: its exit leg and mount are running.</param>
public readonly record struct NavGcFacts(bool Pending, long MsSinceLanding, bool HostQuiet, bool TransportCritical, long Gen0Bytes,
    bool NavigationInFlight = false);

/// <summary>THE post-navigation gen-0 decision, pure. See the file header.</summary>
public static class NavGcPolicy
{
    /// <summary>The page must have landed at least this long ago: its enter leg (<see cref="Design.Nav.EnterDurationMs"/> from the
    /// landing) has run, plus a margin. A gen-0 pause suspends the render thread too, so one inside the enter leg is a hitch in it.</summary>
    public const long QuietMs = (long)Design.Nav.EnterDurationMs + 40;

    /// <summary>No click, key or wheel input for at least this long (hover moves do not count).</summary>
    public const long InteractionQuietMs = 150;

    /// <summary>Less garbage than this in gen 0 is not worth a pause: the next mount fits in what is left of the budget.</summary>
    public const long MinGen0Bytes = 1L << 20;

    /// <summary>A navigation that never goes quiet (a page that animates on, a long scroll right after) stops trying here.</summary>
    public const long GiveUpMs = 4_000;

    public static NavGcVerdict Decide(in NavGcFacts f)
    {
        if (!f.Pending) return NavGcVerdict.Done;
        if (f.NavigationInFlight || f.MsSinceLanding < QuietMs) return NavGcVerdict.Wait;
        if (!f.HostQuiet || f.TransportCritical) return f.MsSinceLanding >= GiveUpMs ? NavGcVerdict.Done : NavGcVerdict.Wait;
        return f.Gen0Bytes >= MinGen0Bytes ? NavGcVerdict.Collect : NavGcVerdict.Done;
    }
}

public static partial class Residency
{
    // ── the post-navigation gen-0 collection (NavGcPolicy) ────────────────────────────────────────────────────────────

    /// <summary>Is the engine host settled? The GUI reads its host (<c>FluentApp.IsSettledForPause</c>); the headless host
    /// points this at its own. Null = quiet.</summary>
    public static Func<bool>? HostQuiet;

    static bool s_navGcPending, s_navInFlight;
    static long s_navLandedMs;
    static int s_seenGen0 = -1;
    static long s_allocAtSeenGen0;

    /// <summary>Total post-navigation collections run (diagnostics and tests).</summary>
    public static int NavGcRuns { get; private set; }

    /// <summary>A navigation was committed (Shell's route effect): until it lands, a pending decision waits, so a settle tick of
    /// the previous navigation cannot collect inside this one's exit leg and mount. UI thread.</summary>
    public static void NoteNavigationStarted() => s_navInFlight = true;

    /// <summary>A navigation's page landed (Shell's landing step; Shell arms the settle timer). UI thread.</summary>
    public static void NoteNavigationLanded()
    {
        ObserveGen0();
        s_navGcPending = true;
        s_navInFlight = false;
        s_navLandedMs = Environment.TickCount64;
    }

    // The gen-0 baseline: the total allocated when the governor first saw the current gen-0 count. Allocation between
    // that GC and this observation is not counted, so Gen0Bytes only ever under-estimates.
    static void ObserveGen0()
    {
        int c = GC.CollectionCount(0);
        if (c == s_seenGen0) return;
        s_seenGen0 = c;
        s_allocAtSeenGen0 = GC.GetTotalAllocatedBytes(precise: false);
    }

    /// <summary>One settle tick (the governor's one-shot timer, posted to the UI thread). Returns true when the decision
    /// is still open (the caller re-arms the timer).</summary>
    public static bool NavGcTick()
    {
        ObserveGen0();
        long now = Environment.TickCount64;
        Playback.Phase phase = Playback.Snap().Phase;
        var facts = new NavGcFacts(
            Pending: s_navGcPending,
            MsSinceLanding: now - s_navLandedMs,
            HostQuiet: HostQuiet?.Invoke() ?? true,
            TransportCritical: phase == Playback.Phase.Loading || Playback.Pending.Load.Peek(),
            Gen0Bytes: GC.GetTotalAllocatedBytes(precise: false) - s_allocAtSeenGen0,
            NavigationInFlight: s_navInFlight);
        switch (NavGcPolicy.Decide(in facts))
        {
            case NavGcVerdict.Wait:
                return true;
            case NavGcVerdict.Collect:
                s_navGcPending = false;
                GC.Collect(0, GCCollectionMode.Forced, blocking: true);
                NavGcRuns++;
                ObserveGen0();
                return false;
            default:
                s_navGcPending = false;
                return false;
        }
    }

    static long s_lastCompactMs = long.MinValue, s_lastBackgroundMs = long.MinValue;
    static bool s_backgroundRanBlocking;

    /// <summary>One heap decision and its action (see the file header). Called by <see cref="Poll"/> on the UI thread.
    /// Returns the committed bytes a compaction handed back (0 for a background request: that one finishes on the GC's
    /// own thread, and the next poll's numbers show it).</summary>
    static long HeapTick(MemoryPressure level, out HeapAction action)
    {
        long now = Environment.TickCount64;
        GCMemoryInfo info = GC.GetGCMemoryInfo();
        Playback.Phase phase = Playback.Snap().Phase;
        long phaseChanged = Playback.PhaseChangedAtMs;
        var facts = new HeapFacts(
            HeapBytes: info.HeapSizeBytes,
            FragmentedBytes: info.FragmentedBytes,
            BaselineBytes: LastFullGcHeapBytes(),
            TransportEngaged: phase is Playback.Phase.Playing or Playback.Phase.Loading || Playback.Pending.Load.Peek(),
            MsSinceTransportChange: phaseChanged == 0 ? long.MaxValue : now - phaseChanged,
            OnScreen: WindowOnScreen(),
            MsSinceCompact: s_lastCompactMs == long.MinValue ? long.MaxValue : now - s_lastCompactMs,
            MsSinceBackground: s_lastBackgroundMs == long.MinValue ? long.MaxValue : now - s_lastBackgroundMs,
            Pressure: level,
            BackgroundRanBlocking: s_backgroundRanBlocking);
        action = HeapPolicy.Decide(in facts);
        switch (action)
        {
            case HeapAction.Background:
            {
                s_lastBackgroundMs = now;
                bool ranBlocking = RequestBackgroundGen2(out double pauseMs);
                if (ranBlocking) s_backgroundRanBlocking = true;
                Log.Event(ranBlocking ? WaveeLogLevel.Warning : WaveeLogLevel.Info, "memory", "heap.background",
                    $"requested a background gen2: blocking={ranBlocking} pauseMs={pauseMs:0.0} engaged={facts.TransportEngaged} heap={info.HeapSizeBytes >> 20}MB",
                    null, -1, null);
                return 0;
            }
            case HeapAction.Compact:
            {
                s_lastCompactMs = now;
                s_lastBackgroundMs = now;
                long committed = info.TotalCommittedBytes;
                TimeSpan pauseBefore = GC.GetTotalPauseDuration();
                var sw = Stopwatch.StartNew();
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                GCMemoryInfo after = GC.GetGCMemoryInfo();
                Log.Info("memory", $"heap.compact heap={info.HeapSizeBytes >> 20}->{after.HeapSizeBytes >> 20}MB committed={committed >> 20}->{after.TotalCommittedBytes >> 20}MB "
                    + $"fragmented={info.FragmentedBytes >> 20}MB pauseMs={(GC.GetTotalPauseDuration() - pauseBefore).TotalMilliseconds:0} wallMs={sw.ElapsedMilliseconds}");
                return Math.Max(0, committed - after.TotalCommittedBytes);
            }
            default:
                return 0;
        }
    }

    /// <summary>Ask for one background gen2 and report how the runtime actually ran it: true when it ran a BLOCKING full
    /// collection instead. With concurrent GC on a non-blocking request starts a background gen2, but a GC setting
    /// (ConserveMemory, a full region pool) could turn it into a blocking, compacting one — exactly what must never run
    /// while music plays — so every request is measured. <paramref name="pauseMs"/> is the suspension it cost.</summary>
    public static bool RequestBackgroundGen2(out double pauseMs)
    {
        long blockingBefore = GC.GetGCMemoryInfo(GCKind.FullBlocking).Index;
        TimeSpan pauseBefore = GC.GetTotalPauseDuration();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: false, compacting: false);
        pauseMs = (GC.GetTotalPauseDuration() - pauseBefore).TotalMilliseconds;
        return GC.GetGCMemoryInfo(GCKind.FullBlocking).Index != blockingBefore;
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

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint hwnd);
}
