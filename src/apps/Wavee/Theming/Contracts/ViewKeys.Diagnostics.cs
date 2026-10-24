// ── Theming/Contracts/ViewKeys.Diagnostics.cs — the Common diagnostics and logs pages' published view keys (themes plan 6.0, 6.5, T11c) ──
//
// T11c publishes the developer pages that every theme draws with the same Common blocks (`Themes\Common\CommonDiagnostics*.cs`,
// `CommonLogs*.cs`; Classic registers them too, so there is one copy of each view): the three diagnostics pages (playback runtime,
// Connect, realtime capture) and the log viewer (`Pages.Logs`).
//
// WHAT STAYS CORE. The diagnostics pages are documents: the core builds a list of `DiagNode`s (cards, rows, chips, buttons, the
// capture roots and their trees) from the runtime report, the provisioner status, the module host, the updater, the Connect traces, the
// capture store and the engine's tile census, and re-runs the builder inside the view's render so every signal it reads subscribes the
// view. The view draws the nodes. The five widgets that own state of their own (the playback health card with its 1 Hz read, the podcast
// wire check, the scroll probe card, the tiles card with its GPU pass toggle, the evidence card with its pixel query) are `DiagNode`s
// whose hosts are core objects. The log viewer's sessions (the folder walk, the load of a past session, the label and selection rules)
// are the core `LogsPage.Sessions` model behind <see cref="ILogsHost"/>; the view owns the filter, the level strip, the order, the wrap,
// the expanded row and the list.

using FluentGpu.Theming;

namespace Wavee.Theming.Contracts;

public static partial class ViewKeys
{
    /// <summary>The `playback-diagnostics` page ("Why is local playback not ready?"). Mounted by `Diagnostics.RuntimePage`. Props: <see cref="DiagnosticsPageProps"/>.</summary>
    public static readonly ViewKey<DiagnosticsPageProps> DiagnosticsPlayback = new("Pages.Diagnostics.Playback");

    /// <summary>The `connect-diagnostics` page (who owns playback, the last cluster, put-state and echo). Mounted by `Diagnostics.ConnectPage`. Props: <see cref="DiagnosticsPageProps"/>.</summary>
    public static readonly ViewKey<DiagnosticsPageProps> DiagnosticsConnect = new("Pages.Diagnostics.Connect");

    /// <summary>The `capture-diagnostics` page (the realtime capture, the scroll probe, the tile census, the evidence card). Mounted by `Diagnostics.CapturePage`. Props: <see cref="DiagnosticsPageProps"/>.</summary>
    public static readonly ViewKey<DiagnosticsPageProps> DiagnosticsCapture = new("Pages.Diagnostics.Capture");

    /// <summary>The `logs` page (the log viewer). Mounted by the core `LogsPage.Host`. Props: <see cref="LogsPageProps"/>.</summary>
    public static readonly ViewKey<LogsPageProps> LogsPage = new("Pages.Logs");
}
