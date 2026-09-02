namespace Wavee;

/// <summary>
/// Outcome of the PREVIOUS run, read at the start of this one via <see cref="RunMarker.Begin"/>.
/// </summary>
public enum RunOutcome : byte { Unknown, Clean, Unclean }

/// <summary>
/// A one-value "was the last run clean" breadcrumb, bracketing every launch: <see cref="Begin"/> at startup reads
/// the marker the PREVIOUS process left and immediately overwrites it with "running"; <see cref="End"/> at orderly
/// shutdown (ProcessExit, or after the app loop returns) flips it to "clean". A marker still reading "running" the
/// next time <see cref="Begin"/> runs means the previous process never got to run its shutdown path — a crash, a
/// kill, or an OS-forced termination (logoff during a hung shutdown reads the same way; see CrashPromptPolicy).
/// <para><see cref="End"/> and <see cref="MarkCrashed"/> also clear <c>WaveeSettings.UncleanExitOffered</c>: the
/// evidence-free "unclean exit" prompt is offered once per streak of unclean runs, and a streak ends only here.</para>
/// </summary>
static class RunMarker
{
    // "Crashed" is written by a managed exception/app-loop handler that already wrote its own crash report — it
    // must never be downgraded to Unclean on the next Begin (that would double-count the same crash).
    public const string Running = "running", Clean = "clean", Crashed = "crashed";

    public static RunOutcome Begin(IAppSettings s)
    {
        string prev = s.Get(WaveeSettings.RunMarker);
        s.Set(WaveeSettings.RunMarker, Running);
        // "crashed" reads as Unclean, not Clean: CrashPromptPolicy ranks ManagedReport > WerDump > UncleanExit, so when a
        // handler DID write its report the marker never matters — and when the write failed (a handler ran, the file
        // did not land), Clean here silently suppressed the one fallback that would still have offered the crash.
        return prev switch
        {
            "" => RunOutcome.Unknown,
            Running or Crashed => RunOutcome.Unclean,
            _ => RunOutcome.Clean,
        };
    }

    public static void End(IAppSettings s)
    {
        // Only downgrade our own "running" mark — never stomp a "crashed" a handler just wrote for THIS run.
        if (s.Get(WaveeSettings.RunMarker) == Running)
            s.Set(WaveeSettings.RunMarker, Clean);
        // An orderly exit ends an unclean STREAK: the next stale-"running" marker is a fresh, offer-worthy event
        // again (CrashPromptPolicy.Decide's uncleanExitOffered gate).
        s.Set(WaveeSettings.UncleanExitOffered, false);
    }

    public static void MarkCrashed(IAppSettings s)
    {
        s.Set(WaveeSettings.RunMarker, Crashed);
        // A crash with a report is a new conversation with the user (ManagedReport ranks first next launch); it also
        // re-arms the evidence-free offer so a kill AFTER that report is offered once more rather than swallowed.
        s.Set(WaveeSettings.UncleanExitOffered, false);
    }
}
