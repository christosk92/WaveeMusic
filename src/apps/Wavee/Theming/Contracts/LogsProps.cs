// ── Theming/Contracts/LogsProps.cs — the props of the Common log viewer page (themes plan 6.5, T11c) ───────────────────────────
//
// The log viewer is a page with a state machine behind it: the folder walk that lists the past sessions, the load of one past session,
// the label and selection rules, the export and the verbs. All of that is the core `LogsPage.Sessions` model, reached through
// <see cref="ILogsHost"/> (one stable object per page mount: its signals and methods subscribe the reading component). What the
// view keeps is the picture and the state that only the picture has: the filter text, the level strip, the category, the order, the
// grouping, the wrap, the page cap, the expanded row, the list's controller and the "…" menu.

using FluentGpu.Signals;

namespace Wavee.Theming.Contracts;

/// <summary>The props of <see cref="ViewKeys.LogsPage"/>.</summary>
public sealed record LogsPageProps
{
    /// <summary>The session model and the verbs (the same object for the page's whole life).</summary>
    public required ILogsHost Host { get; init; }
}

/// <summary>The log viewer's session model, verbs and facts, read by the Common view.</summary>
public interface ILogsHost
{
    /// <summary>The session picker's selection (0: this run; n: the nth past session, newest first). The picker's combo writes it.</summary>
    Signal<int> Session { get; }

    /// <summary>Moves on the live poll, a load landing, Clear view, Verbose and a page activation (the view re-renders on it).</summary>
    IReadSignal<int> Refresh { get; }

    /// <summary>Moves ONLY when the session list (or its status) changes: the picker remounts on it so its labels (the live uptime) are fresh.</summary>
    IReadSignal<int> SessionsRev { get; }

    /// <summary>Re-renders the view (a bump of <see cref="Refresh"/>).</summary>
    void Bump();

    /// <summary>The log's version: the live tail bumps only when this moved.</summary>
    long LogVersion { get; }

    /// <summary>One off-thread walk of the log folder (never latches; a thrown walk is a caption).</summary>
    void RefreshSessions(Action<Action> post, bool force = false);

    /// <summary>Loads the selected past session off-thread when it is not the cached one.</summary>
    void EnsureSessionLoaded(Action<Action> post);

    /// <summary>The picker's labels: this run first (with its live uptime), then every past run newest first.</summary>
    string[] SessionLabels();

    /// <summary>The picker's status line ("reading", "none on disk", "couldn't read the log folder"), or null.</summary>
    string? PickerCaption();

    /// <summary>The entries the view shows: the whole live ring, the selected past session once it is loaded, or null while it loads.
    /// <paramref name="past"/> is the selected past session (null for the live one).</summary>
    WaveeLogEntry[]? EntriesFor(bool live, out WaveeLogSessions.Info? past);

    /// <summary>Exports the live ring or the selected past session through the Save As picker.</summary>
    void ExportSession();

    /// <summary>The "…" menu's Verbose item: the current state.</summary>
    bool Verbose { get; }

    /// <summary>Turns the verbose capture level on or off.</summary>
    void SetVerbose(bool on);

    /// <summary>Clears the live ring (the view confirms first).</summary>
    void ClearRing();

    /// <summary>Opens the feedback form on a bug report quoting the selected session.</summary>
    void ReportThisSession();

    /// <summary>Opens the log folder.</summary>
    void OpenLogFolder();

    /// <summary>A breadcrumb was invoked (0: Settings on its remembered tab; 1: the tab this page belongs to; the last is this page).</summary>
    void OpenCrumb(int index);

    /// <summary>The page's route is the active one (Esc leaves only then).</summary>
    bool IsActive { get; }

    /// <summary>The ONE exit, shared by Back and Esc: the real Back when there is a stack, else the Privacy and diagnostics tab.</summary>
    void GoBack();

    /// <summary>The capture caption's two level names: what the ring keeps and what the file keeps.</summary>
    (string Ring, string File) CaptureLevels();
}
