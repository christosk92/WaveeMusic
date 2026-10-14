// ── Screens/LogsPage.Host.cs ───────────────────────────────────────────────────────────────────────────────────────────
// the Logs page's CORE HOST: the route's mount point, the session model (the folder walk, the load of a past session, the picker's
// labels and caption, where a selection lands, the export and the verbs) and nothing that draws
//
// Role: CORE host (the picture — the header with its breadcrumbs, session picker and Open folder, the toolbar, the column header,
//   the measured virtualised list with its row anatomy, the footer — is the Common `Pages.Logs` view: `Themes\Common\CommonLogs*.cs`)
// Owner: S
// Wave: 0.3 privacy-diagnostics-tab (docs/plans/wavee/privacy-diagnostics-tab-implementation.md §3.2, §5.2, §5.7)
// Spec: docs/themes/plan/technical-plan.md §6.5 (T11c)
//
// THE RULE THIS FILE KEEPS: it decides, it never lays out. The filter/group/cap is `Diagnostics.LogView`, the capture levels are
// `LogCapturePolicy`, the strip's labels / the picker's caption kind / where a selection lands after a re-list are `LogsPage.Rules`
// (`LogsPage.cs`, tested); the disk walk and the export write are `LogStore`. This file owns the state those need: the session list,
// the busy flags, the loaded past session and the three signals the view reads (the picker's selection, the refresh bump and the
// session list's revision).
//
// The sessions are re-listed once on mount and once per page activation (the view runs both: `UseActivation` fires on TRANSITIONS only,
// which is why the mount walk is its own effect). A walk NEVER latches: the busy flag clears in a `finally` inside the posted
// continuation AND when the post itself fails; a thrown walk is a caption ("couldn't read the log folder") rather than a swallowed
// fault.

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using Slots = FluentGpu.Theming.Slots;

namespace Wavee;

public static partial class LogsPage
{
    /// <summary>The `logs` route's page (registered by <c>Diagnostics.InstallPages</c> through <c>Shell.SetPage</c>).</summary>
    // MOUNT POINT (stage B contract)
    public static Element Page() => Embed.Comp(static () => new Host());

    static TimeSpan LocalOffset => TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.UtcNow);

    static string LevelName(WaveeLogLevel level) => Diagnostics.LogView.LevelNames[Math.Clamp((int)level, 0, Diagnostics.LogView.LevelNames.Length - 1)];

    /// <summary>The identity a loaded past session is cached under: its key plus its entry count, so a re-list that grew or
    /// pruned the same run never serves the stale rows.</summary>
    static string LoadKey(WaveeLogSessions.Info info) => WaveeLogSessions.KeyOf(info) + "#" + info.EntryCount.ToString(CultureInfo.InvariantCulture);

    /// <summary>One mount of the page: the session model is created with it and lives and dies with it (the old view's fields).</summary>
    [CoreHost]
    sealed class Host : Component
    {
        readonly LogsPageProps _props = new() { Host = new Sessions() };

        public override Element Render() => Slots.View(ViewKeys.LogsPage, _props);
    }

    /// <summary>The viewer's session model. Every member runs on the UI thread except the pool-thread bodies named in
    /// <see cref="RefreshSessions"/> and <see cref="EnsureSessionLoaded"/>.</summary>
    sealed class Sessions : ILogsHost
    {
        readonly Signal<int> _session = new(0);
        readonly Signal<int> _refresh = new(0);            // the live poll, a load landing, Clear view, Verbose, a page activation
        readonly Signal<int> _sessionsRev = new(0);        // ONLY when the session LIST (or its status) changes

        List<WaveeLogSessions.Info>? _sessions;
        bool _sessionsBusy, _sessionsFailed, _sessionLoadBusy;
        WaveeLogEntry[]? _sessionEntries;
        string? _loadedKey;

        public Signal<int> Session => _session;

        public IReadSignal<int> Refresh => _refresh;

        public IReadSignal<int> SessionsRev => _sessionsRev;

        public void Bump() => _refresh.Value = _refresh.Peek() + 1;

        public long LogVersion => Log.Version;

        public bool IsActive => Shell.Current.Peek().Kind == Shell.RouteKind.Logs;

        /// <summary>The ONE exit, shared by Back and Esc: the shell's real Back when there is a stack; a page with none (a deep
        /// link, a restored tab) lands on the tab that owns it.</summary>
        public void GoBack()
        {
            if (Shell.CanBack.Peek()) Shell.GoBack();
            else Settings.Open(Settings.Tab.PrivacyDiagnostics);
        }

        /// <summary>Settings › Privacy &amp; diagnostics › Logs. The first crumb opens Settings on its remembered tab; the
        /// second lands on the tab this page belongs to; the last is this page and does nothing.</summary>
        public void OpenCrumb(int index)
        {
            if (index == 0) Shell.GoTo(new Shell.Route(Shell.RouteKind.Settings));
            else if (index == 1) Settings.Open(Settings.Tab.PrivacyDiagnostics);
        }

        public void OpenLogFolder() => Diagnostics.OpenFolder(Path.GetDirectoryName(Log.FilePath ?? ""));

        public bool Verbose => LogStore.IsVerbose();

        public void SetVerbose(bool on) => LogStore.SetVerbose(on);

        public void ClearRing() => LogStore.ClearRing();

        public void ReportThisSession() => Feedback.Open(Feedback.ReportKind.Bug,
            new Feedback.ReportPrefill(PastSessionId: SelectedPastSession() is { } s ? WaveeLogSessions.KeyOf(s) : null));

        public (string Ring, string File) CaptureLevels()
        {
            var min = Log.MinLevel;
            var file = LogCapturePolicy.EffectiveFileLevel(min, Log.FileMinLevel);
            return (LevelName(min), LevelName(file));
        }

        public WaveeLogEntry[]? EntriesFor(bool live, out WaveeLogSessions.Info? past)
        {
            past = live ? null : SelectedPastSession();
            return live ? Log.Snapshot()
                : past is not null && _loadedKey is not null && string.Equals(_loadedKey, LoadKey(past), StringComparison.Ordinal) ? _sessionEntries
                : null;
        }

        /// <summary>The picker's status line (G4: "This session" used to read identically for "still walking", "none on
        /// disk" and "the walk faulted"). A re-list over an existing list never flashes "reading": a list is on screen.</summary>
        public string? PickerCaption()
        {
            bool busy = _sessions is null && !_sessionsFailed;
            int count = _sessions?.Count ?? 0;
            return Rules.PickerCaption(busy, count, _sessionsFailed) switch
            {
                Rules.PickerCaptionKind.Reading => Loc.Get(Strings.Logs.SessionsReading),
                Rules.PickerCaptionKind.NoneOnDisk => Strings.Logs.SessionsNone(Log.RetentionDays),
                Rules.PickerCaptionKind.Failed => Loc.Get(Strings.Logs.SessionsFailed),
                _ => null,
            };
        }

        /// <summary>One off-thread walk of the log folder. NEVER latches: the busy flag clears in a <c>finally</c> inside the
        /// posted continuation AND when the post itself fails, a thrown walk is a caption ("couldn't read the log folder")
        /// rather than a swallowed fault, and every walk logs one <c>log.sessions.listed</c> line (G4).</summary>
        public void RefreshSessions(Action<Action> post, bool force = false)
        {
            if (_sessionsBusy || (!force && _sessions is not null)) return;
            _sessionsBusy = true;
            string? basePath = LogStore.BasePath;   // the BASE (wavee.log): the dated live file would glob one day's rolls
            int pid = Environment.ProcessId;
            _ = Task.Run(() =>
            {
                var outcome = LogStore.Walk(basePath, pid);   // the disk walk and its one `log.sessions.listed` line
                var walk = outcome.Walk;
                var fault = outcome.Fault;
                try
                {
                    post(() =>
                    {
                        try { ApplyWalk(walk, fault); }
                        finally { _sessionsBusy = false; }
                    });
                }
                catch { _sessionsBusy = false; }   // the poster itself failed: never leave the page unable to re-list
            });
        }

        /// <summary>UI thread. The selection follows the session's KEY across the re-list (indices shift when a run appears
        /// or is pruned), the combo remounts through <see cref="SessionsRev"/> so its labels (the live uptime) are fresh,
        /// and a loaded past session stays cached under its key.</summary>
        void ApplyWalk(WaveeLogSessions.WalkResult? walk, Exception? fault)
        {
            string? keep = SelectedPastSession() is { } selected ? WaveeLogSessions.KeyOf(selected) : null;
            _sessionsFailed = fault is not null || walk?.Error is not null;
            if (walk is not null)
            {
                _sessions = walk.Sessions;
                var keys = new string[walk.Sessions.Count];
                for (int i = 0; i < keys.Length; i++) keys[i] = WaveeLogSessions.KeyOf(walk.Sessions[i]);
                _session.SetIfChanged(Rules.SelectionAfterRefresh(keep, keys));
            }
            _sessionsRev.Value = _sessionsRev.Peek() + 1;
        }

        public void EnsureSessionLoaded(Action<Action> post)
        {
            int sel = _session.Peek();
            if (sel == 0 || _sessionLoadBusy || _sessions is not { } sessions || sel - 1 >= sessions.Count) return;
            var info = sessions[sel - 1];
            string key = LoadKey(info);
            if (string.Equals(_loadedKey, key, StringComparison.Ordinal)) return;
            _sessionLoadBusy = true;
            _ = Task.Run(() =>
            {
                WaveeLogEntry[]? loaded = LogStore.Load(info);
                post(() =>
                {
                    _sessionEntries = loaded ?? [];
                    _loadedKey = key;
                    _sessionLoadBusy = false;
                    Bump();
                    EnsureSessionLoaded(post);   // the selection may have moved while this one loaded
                });
            });
        }

        /// <summary>The picker's labels: this run first (with its live uptime), then every past run newest first.</summary>
        public string[] SessionLabels()
        {
            int n = 1 + (_sessions?.Count ?? 0);
            var labels = new string[n];
            labels[0] = Strings.Logs.ThisSession(Environment.ProcessId, Diagnostics.LogView.Uptime(TimeSpan.FromMilliseconds(Log.SinceStartMs)));
            if (_sessions is { } list)
                for (int i = 0; i < list.Count; i++)
                    labels[i + 1] = Diagnostics.LogView.SessionLabel(list[i].StartUnixMs, list[i].Pid, list[i].EntryCount, LocalOffset);
            return labels;
        }

        WaveeLogSessions.Info? SelectedPastSession()
        {
            int sel = _session.Peek();
            return sel == 0 || _sessions is not { } sessions || sel - 1 >= sessions.Count ? null : sessions[sel - 1];
        }

        /// <summary>Live: the WHOLE ring in the file-line shape (with <c>t=</c>), independent of every filter — "Copy
        /// visible" is the filtered one. A past session exports its raw lines.</summary>
        public void ExportSession()
        {
            bool live = _session.Peek() == 0;
            var past = live ? null : SelectedPastSession();
            if (!live && past is null) return;
            // The Save As dialog runs on its own thread (Pickers, #155); the export happens when it answers, UI thread.
            Pickers.Pick(
                PickerRequest.Save(Loc.Get(Strings.Logs.ExportSession),
                    LogStore.ExportFileName(past),
                    (Loc.Get(Strings.Logs.ExportLogText), "*.txt"), (Loc.Get(Strings.Logs.ExportAllFiles), "*.*")),
                done: path =>
                {
                    if (path is null) return;
                    LogStore.Export(path, past);
                },
                failed: ex => Log.Warn("log", "the export dialog failed", ex));
        }
    }
}
