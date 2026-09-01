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
        return prev switch
        {
            "" => RunOutcome.Unknown,
            Running => RunOutcome.Unclean,
            _ => RunOutcome.Clean,
        };
    }

    public static void End(IAppSettings s)
    {
        // Only downgrade our own "running" mark — never stomp a "crashed" a handler just wrote for THIS run.
        if (s.Get(WaveeSettings.RunMarker) == Running)
            s.Set(WaveeSettings.RunMarker, Clean);
    }

    public static void MarkCrashed(IAppSettings s) => s.Set(WaveeSettings.RunMarker, Crashed);
}
