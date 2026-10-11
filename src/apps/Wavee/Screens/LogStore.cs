// ── Screens/LogStore.cs ────────────────────────────────────────────────────────────────────────────────────────────────
// the Logs page's DISK model: the off-thread walk of the log folder, the load of one past session, the session export,
// and the capture-level and ring verbs the page's "…" menu calls
//
// Role: CORE (engine-free: files, the log ring and the settings; no element, component or overlay type)
// Spec: docs/themes/plan/technical-plan.md §5.1 C15 (X38)
//
// Moved out of `LogsPage.UI.cs` unchanged: the walk with its one `log.sessions.listed` line (G4), the guarded load of a
// past session, the export write for the live ring or a past session, and the verbose / clear-view verbs. The pure rules
// stay `LogsPage.Rules` and `Diagnostics.LogView`; the disk walk itself is `WaveeLogSessions`. The page keeps the pickers,
// the posts back to the UI thread and every signal.

namespace Wavee;

public static class LogStore
{
    /// <summary>One walk's outcome: the walk (null when it faulted), the fault, and how long it took.</summary>
    public readonly record struct WalkOutcome(WaveeLogSessions.WalkResult? Walk, Exception? Fault, long ElapsedMs)
    {
        /// <summary>Did the walk fail or come back with an error? The picker then says "couldn't read the log folder".</summary>
        public bool Failed => Fault is not null || Walk?.Error is not null;
    }

    /// <summary>The BASE log path (wavee.log): the dated live file would glob one day's rolls. Read on the UI thread, then
    /// handed to <see cref="Walk"/>.</summary>
    public static string? BasePath => Log.BasePath;

    /// <summary>Pool thread: one walk of the log folder. NEVER throws and never latches: a thrown walk comes back as
    /// <see cref="WalkOutcome.Fault"/>, and every walk logs one <c>log.sessions.listed</c> line.</summary>
    public static WalkOutcome Walk(string? basePath, int pid)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        WaveeLogSessions.WalkResult? walk = null;
        Exception? fault = null;
        try { walk = WaveeLogSessions.ListPastSessions(basePath, pid); }
        catch (Exception ex) { fault = ex; }
        long ms = (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var outcome = new WalkOutcome(walk, fault, ms);
        LogWalk(outcome);
        return outcome;
    }

    static void LogWalk(in WalkOutcome outcome)
    {
        var walk = outcome.Walk;
        var error = outcome.Fault ?? walk?.Error;
        if (walk is null)
        {
            Log.Event(WaveeLogLevel.Warning, "log", "sessions.listed", "past sessions could not be listed", null, outcome.ElapsedMs, error);
            return;
        }
        var level = error is null ? WaveeLogLevel.Info : WaveeLogLevel.Warning;
        Log.Event(level, "log", "sessions.listed", error is null ? "past sessions listed" : "past sessions listed with an error", null, outcome.ElapsedMs, error,
            WaveeLogField.Of("files", walk.FilesSeen), WaveeLogField.Of("lines", walk.LinesSeen), WaveeLogField.Of("sessions", walk.Sessions.Count));
    }

    /// <summary>Pool thread: one past session's entries, or null when it could not be read (logged).</summary>
    public static WaveeLogEntry[]? Load(WaveeLogSessions.Info info)
    {
        try { return WaveeLogSessions.LoadSession(info); }
        catch (Exception ex) { Log.Warn("log", "a past session could not be read", ex); return null; }
    }

    /// <summary>The Save As dialog's suggested name: the live ring or the past session's key.</summary>
    public static string ExportFileName(WaveeLogSessions.Info? past)
        => past is null ? "wavee-session-live.txt" : "wavee-session-" + WaveeLogSessions.KeyOf(past) + ".txt";

    /// <summary>Write the export: the WHOLE live ring in the file-line shape (<paramref name="past"/> null), independent of
    /// every filter, or a past session's raw lines. An I/O failure is logged, not thrown. True when the file was written.</summary>
    public static bool Export(string path, WaveeLogSessions.Info? past)
    {
        try
        {
            if (past is null) File.WriteAllText(path, Diagnostics.LogView.ExportText(Log.Snapshot()));
            else WaveeLogSessions.ExportSessionToFile(past, path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("log", "the session export failed", ex);
            return false;
        }
    }

    // ── the "…" menu's verbs ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Is the capture level Verbose (Debug and below)?</summary>
    public static bool IsVerbose() => LogCapturePolicy.IsVerbose(Log.MinLevel);

    /// <summary>Switch Verbose capture on or off (persisted through the typed log-level keys).</summary>
    public static void SetVerbose(bool on) => LogCapturePolicy.SetVerbose(Platform.Settings, on);

    /// <summary>"Clear view": empty the live ring.</summary>
    public static void ClearRing() => Log.ClearRing();
}
