namespace Wavee;

/// <summary>Where the evidence that the previous run crashed came from, strongest first.</summary>
public enum CrashSource : byte { None, ManagedReport, WerDump, UncleanExit }

/// <summary>How this launch should surface the crash to the user.</summary>
public enum CrashPromptMode : byte { None, Dialog, Toast }

public readonly record struct CrashPromptDecision(CrashPromptMode Mode, CrashSource Source, string? ReportPath, string? DumpPath);

/// <summary>
/// Decides, once per launch, whether and how to tell the user the previous run crashed. Engine-free and pure so it
/// can be unit-tested without booting the app; Program.cs computes the decision and latches it on
/// <see cref="ThisLaunch"/>, and ReportChrome consumes it (see the "As built" notes for the exact wiring).
/// </summary>
static class CrashPromptPolicy
{
    /// <summary>Latched by Program.cs for this launch; ReportChrome consumes it (resets to default) — the same
    /// one-shot discipline AfterUpdateDialog uses for its own "show once after launch" latch.</summary>
    public static CrashPromptDecision ThisLaunch;

    /// <param name="versionChanged">True when the previous process was killed by an update deployment (a relaunch
    /// after an update, or the running version no longer matches the last-recorded one) rather than by a crash —
    /// suppresses ONLY the weakest signal (UncleanExit), never a managed report or a WER dump.</param>
    public static CrashPromptDecision Decide(string pendingReport, string? newDumpPath, RunOutcome previousRun, bool optOut, bool versionChanged)
    {
        CrashSource src = pendingReport.Length > 0 ? CrashSource.ManagedReport
                        : newDumpPath is { Length: > 0 } ? CrashSource.WerDump
                        : previousRun == RunOutcome.Unclean && !versionChanged ? CrashSource.UncleanExit
                        : CrashSource.None;
        if (src == CrashSource.None)
            return default;

        return new CrashPromptDecision(
            optOut ? CrashPromptMode.Toast : CrashPromptMode.Dialog,
            src,
            src == CrashSource.ManagedReport ? pendingReport : null,
            newDumpPath);
    }
}
