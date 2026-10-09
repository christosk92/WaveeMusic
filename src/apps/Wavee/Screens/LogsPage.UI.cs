// ── Screens/LogsPage.UI.cs ─────────────────────────────────────────────────────────────────────────────────────────
// the Logs page (route `logs`): a full page of its own that replaces the Settings › Logs tab's embedded viewer. It re-hosts
// the working viewer (the `LogView` pure model, the keyed session / category combos, the measured virtualised list, the row
// anatomy, the 750 ms live poll) under one header, one toolbar and a list card with a column header
//
// Role: UI
// Owner: S
// Wave: 0.3 privacy-diagnostics-tab (docs/plans/wavee/privacy-diagnostics-tab-implementation.md §3.2, §5.2, §5.7)
//
// THE RULE THIS FILE KEEPS: it lays out and binds, it never decides. The filter/group/cap is `Diagnostics.LogView`, the
// capture levels are `LogCapturePolicy`, the strip's labels / the picker's caption / where a selection lands after a
// re-list are `LogsPage.Rules` (`LogsPage.cs`, tested); the disk walk is `WaveeLogSessions` (`Diagnostics.Host.cs`).
//
// FRAME: the page frame the old sidebar customizer used (deleted in the sidebar rework): a 64-DIP header (Back,
// eyebrow + title), Esc → back, `Shell.GoBack()` when there is a back stack, else the Privacy & diagnostics tab. The
// capture level and file level combos left the viewer (they are Settings › Privacy & diagnostics › Logs › Detail
// level); Verbose stays here as a checkable item of the "…" menu.
//
// NOTHING HERE READS A CLOCK PER FRAME (ch 27 N12): the live tail polls at 750 ms and bumps only when `Log.Version` moved
// (and not at all on a past session). The sessions are re-listed once on mount and once per page activation (a keep-alive
// return, or the window coming back) — `UseActivation` fires on TRANSITIONS only, which is why the mount walk is its own
// effect.
//
// PROPS FREEZE AT MOUNT: every control whose interesting fields freeze (the session / category combos, the virtualised
// list) is REMOUNTED through a `Key` that changes exactly when its frozen input does, and every write that tears down the
// node that was clicked runs through `UsePost` so the click finishes first. No signal is written in render.

using System.Globalization;
using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using LogLevelBucket = Wavee.Diagnostics.LogLevelBucket;
using LogView = Wavee.Diagnostics.LogView;
using LogViewQuery = Wavee.Diagnostics.LogViewQuery;
using LogViewResult = Wavee.Diagnostics.LogViewResult;
using LogViewRow = Wavee.Diagnostics.LogViewRow;

namespace Wavee;

public static partial class LogsPage
{
    /// <summary>The `logs` route's page (registered by <c>Diagnostics.Install</c> through <c>Shell.SetPage</c>).</summary>
    // MOUNT POINT (stage B contract)
    public static Element Page() => Embed.Comp(static () => new LogsPageView());

    const float GutterX = 36f, ColumnHeaderHeight = 32f;

    // The row's fixed columns — ONE copy, read by the rows and the column header so they can never drift apart.
    const float ChevronSlot = 12f, DotSlot = 6f, TimeColumn = 92f, LevelColumn = 58f, CategoryColumn = 96f;

    static TimeSpan LocalOffset => TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.UtcNow);

    static bool IsActiveRoute => Shell.Current.Peek().Kind == Shell.RouteKind.Logs;

    /// <summary>The ONE exit, shared by Back and Esc: the shell's real Back when there is a stack; a page with none (a deep
    /// link, a restored tab) lands on the tab that owns it.</summary>
    static void GoBack()
    {
        if (Shell.CanBack.Peek()) Shell.GoBack();
        else Settings.Open(Settings.Tab.PrivacyDiagnostics);
    }

    static string LevelName(WaveeLogLevel level) => LogView.LevelNames[Math.Clamp((int)level, 0, LogView.LevelNames.Length - 1)];

    /// <summary>The identity a loaded past session is cached under: its key plus its entry count, so a re-list that grew or
    /// pruned the same run never serves the stale rows.</summary>
    static string LoadKey(WaveeLogSessions.Info info) => WaveeLogSessions.KeyOf(info) + "#" + info.EntryCount.ToString(CultureInfo.InvariantCulture);

    /// <summary>The viewer. <see cref="_rows"/> and <see cref="_categories"/> are FIELDS, refreshed at the top of every
    /// render, so a command built by an OLDER render (the toolbar does not remount for a plain search edit) still reads the
    /// CURRENT rows when clicked.</summary>
    sealed class LogsPageView : Component
    {
        readonly Signal<string> _search = new("");
        readonly Signal<int> _level = new((int)LogLevelBucket.All);
        readonly Signal<int> _category = new(0);
        readonly Signal<int> _session = new(0);
        readonly Signal<int> _newestFirst = new(1);
        readonly Signal<int> _groupRepeats = new(1);
        readonly Signal<int> _wrap = new(0);
        readonly Signal<int> _refresh = new(0);            // the live poll, a load landing, Clear view, Verbose, a page activation
        readonly Signal<int> _visibleLimit = new(LogView.PageRows);
        readonly Signal<int> _sessionsRev = new(0);         // ONLY when the session LIST (or its status) changes
        readonly Signal<long> _expandedSeq = new(-1);

        List<WaveeLogSessions.Info>? _sessions;
        bool _sessionsBusy, _sessionsFailed, _sessionLoadBusy;
        WaveeLogEntry[]? _sessionEntries;
        string? _loadedKey;
        LogViewRow[] _rows = [];
        string[] _categories = [];
        readonly ItemsViewController _listCtrl = new();
        IOverlayService? _overlay;
        OverlayHandle? _menu;
        NodeHandle _moreAnchor = NodeHandle.Null;
        Action<KeyEventArgs>? _onKey;

        public override Element Render()
        {
            var hooks = UseContext(InputHooks.Current);
            var post = UsePost();
            var lastVersion = UseRef(-1L);
            _overlay = UseContext(Overlay.Service);

            // Mount: the first walk. Activation: every later one (a keep-alive return re-reads the folder; it fires on
            // TRANSITIONS only, so it never doubles the mount walk). The footer's capture caption reads levels the Detail
            // level card can change while this page is parked — a bump re-renders it.
            UseEffect(() => RefreshSessions(post), DepKey.Empty);
            UseActivation(onActivated: () =>
            {
                RefreshSessions(post, force: true);
                _refresh.Value = _refresh.Peek() + 1;
            });
            UseSignalEffect(() =>
            {
                _ = _session.Value;
                _expandedSeq.Value = -1;
                _visibleLimit.Value = LogView.PageRows;
                EnsureSessionLoaded(post);
            });
            // The live tail (session 0 only; auto-pauses while parked): bumps only when the log actually moved.
            UseInterval(() =>
            {
                long v = Log.Version;
                if (v == lastVersion.Value) return;
                lastVersion.Value = v;
                _refresh.Value = _refresh.Peek() + 1;
            }, 750f, enabled: _session.Value == 0);
            var logLayout = UseMemo(static () => new MeasuredStackVirtualLayout(estimatedExtent: 36f), DepKey.Empty);

            _ = _refresh.Value; _ = _expandedSeq.Value; _ = _search.Value; _ = _level.Value; _ = _category.Value;
            _ = _newestFirst.Value; _ = _groupRepeats.Value; _ = _wrap.Value; _ = _visibleLimit.Value; _ = _sessionsRev.Value;

            bool live = LogsPage.Rules.IsLive(_session.Value);
            var past = live ? null : SelectedPastSession();
            WaveeLogEntry[]? entries = live ? Log.Snapshot()
                : past is not null && _loadedKey is not null && string.Equals(_loadedKey, LoadKey(past), StringComparison.Ordinal) ? _sessionEntries
                : null;
            _categories = entries is null ? [] : LogView.Categories(entries);
            // The category clamp lives in an effect (a signal write in render is the backwards-write tripwire).
            UseEffect(() => { if (_category.Peek() > _categories.Length) _category.Value = 0; }, _categories.Length);

            var query = Query();
            var result = entries is null ? LogViewResult.Empty : LogView.Build(entries, query);
            _rows = result.Rows;

            return new BoxEl
            {
                Key = "logs-page", Grow = 1f, Shrink = 1f, Direction = 1, MinWidth = 0f, MinHeight = 0f, ClipToBounds = true,
                OnKeyDown = _onKey ??= OnPageKey,
                Children =
                [
                    HeaderBar(),
                    Divider(),
                    Toolbar(result, live, hooks, post),
                    new BoxEl
                    {
                        Margin = new Edges4(GutterX, 0f, GutterX, Spacing.L),
                        Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1, Corners = CornerRadius4.All(Radii.Card),
                        Fill = Tok.FillCardSecondary, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, ClipToBounds = true,
                        Children =
                        [
                            ColumnHeader(post),
                            Divider(),
                            LogBody(entries, result, query, logLayout),
                            Divider(),
                            Footer(result, live, entries, past),
                        ],
                    },
                ],
            };
        }

        static BoxEl Divider() => new() { Height = 1f, Shrink = 0f, Fill = Tok.StrokeDividerDefault };

        /// <summary>Esc leaves (the one exit, same as Back) when nothing inside handled it first (an open combo, a filter
        /// being cleared) — the same rule as the customizer: only with focus inside the page.</summary>
        void OnPageKey(KeyEventArgs e)
        {
            if (e.Handled || e.KeyCode != Keys.Escape || !IsActiveRoute) return;
            e.Handled = true;
            GoBack();
        }

        LogViewQuery Query() => new((LogLevelBucket)_level.Value, CurrentCategory(), _search.Value,
            _newestFirst.Value != 0, _groupRepeats.Value != 0, _visibleLimit.Value);

        string? CurrentCategory()
        {
            int idx = _category.Value;
            return idx <= 0 || idx > _categories.Length ? null : _categories[idx - 1];
        }

        // ── the header: crumbs over the title lane · [picker caption] · session combo · Open folder ─────────────────

        /// <summary>The child-page head the app uses everywhere a page sits under a parent (Profile lists, the artist
        /// discography): a <c>BreadcrumbBar</c> whose earlier crumbs navigate, over the page hero — no back arrow. The title
        /// lane is the only flexible child (Grow 1 · Basis 0 · Shrink 1 · MinWidth 0), so under pressure the title
        /// ellipsizes and the picker cluster holds its width.</summary>
        Element HeaderBar()
        {
            // Every child is keyed: the picker caption comes and goes, and a positional diff would otherwise slide the
            // combo's remount key onto the wrong sibling.
            var kids = new List<Element>(4)
            {
                new BoxEl
                {
                    Key = "logs:title", Direction = 1, Grow = 1f, Basis = 0f, Shrink = 1f, MinWidth = 0f, Justify = FlexJustify.Center, Gap = Spacing.XXS,
                    Children =
                    [
                        // Settings › Privacy & diagnostics › Logs. The first crumb opens Settings on its remembered tab; the
                        // second lands on the tab this page belongs to; the last is this page and does nothing.
                        BreadcrumbBar.Create([Loc.Get(Strings.Nav.Settings), Loc.Get(Strings.Settings.Tabs.Privacy), Loc.Get(Strings.Logs.Title)], static i =>
                        {
                            if (i == 0) Shell.GoTo(new Shell.Route(Shell.RouteKind.Settings));
                            else if (i == 1) Settings.Open(Settings.Tab.PrivacyDiagnostics);
                        }),
                        Design.Type.PageHero(Loc.Get(Strings.Logs.Title)) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    ],
                },
            };
            if (PickerCaptionText() is { } caption)
                kids.Add(Design.Type.MicroMeta(caption) with
                {
                    Key = "logs:caption", Color = Tok.TextTertiary, Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis,
                    MaxWidth = 200f, Shrink = 1f, MinWidth = 0f,
                });
            var labels = SessionLabels();
            kids.Add(ComboBox.Create(labels, _session, width: 320f, onChange: _ => _expandedSeq.Value = -1)
                with { Key = "logs:session:" + _sessionsRev.Value.ToString(CultureInfo.InvariantCulture) });
            kids.Add(Button.Create(Loc.Get(Strings.Logs.OpenFolder), static () => Diagnostics.OpenFolder(Path.GetDirectoryName(Log.FilePath ?? "")),
                ButtonAppearance.Standard, ControlSize.Small, glyph: Icons.Folder) with { Key = "logs:folder", Shrink = 0f });

            return new BoxEl
            {
                // The page gutter (PageWide) and the hero's top room, as Profile.Lists.Page's head; the picker cluster
                // sits on the hero's baseline row, so it centres against the two-line title lane.
                Key = "logs-header", Direction = 0, Shrink = 0f, Gap = Spacing.S,
                AlignItems = FlexAlign.Center, Padding = new Edges4(Spacing.PageWide, Spacing.L, Spacing.PageWide, Spacing.S),
                Children = kids.ToArray(),
            };
        }

        /// <summary>The picker's status line (G4: "This session" used to read identically for "still walking", "none on
        /// disk" and "the walk faulted"). A re-list over an existing list never flashes "reading": a list is on screen.</summary>
        string? PickerCaptionText()
        {
            bool busy = _sessions is null && !_sessionsFailed;
            int count = _sessions?.Count ?? 0;
            return LogsPage.Rules.PickerCaption(busy, count, _sessionsFailed) switch
            {
                LogsPage.Rules.PickerCaptionKind.Reading => Loc.Get(Strings.Logs.SessionsReading),
                LogsPage.Rules.PickerCaptionKind.NoneOnDisk => Strings.Logs.SessionsNone(Log.RetentionDays),
                LogsPage.Rules.PickerCaptionKind.Failed => Loc.Get(Strings.Logs.SessionsFailed),
                _ => null,
            };
        }

        // ── the toolbar: filter · level strip (counts inside) · categories · … · copy · export · more ─────────────────

        Element Toolbar(LogViewResult result, bool live, InputHooks hooks, Action<Action> post)
        {
            var catLabels = new string[_categories.Length + 1];
            catLabels[0] = Loc.Get(Strings.Logs.AllCategories);
            Array.Copy(_categories, 0, catLabels, 1, _categories.Length);

            // Rebuilt every render: Segmented re-pushes its props, so the labels follow the live counts with no remount.
            var texts = LogsPage.Rules.LevelLabels(Loc.Get(Strings.Logs.LevelAll), Loc.Get(Strings.Logs.LevelInfo),
                Loc.Get(Strings.Logs.LevelWarnings), Loc.Get(Strings.Logs.LevelErrors), result.WarningCount, result.ErrorCount);
            var levelItems = new SegmentedItem[texts.Length];
            for (int i = 0; i < texts.Length; i++) levelItems[i] = new SegmentedItem(texts[i]);

            string exportTip = live
                ? Loc.Get(Strings.Logs.ExportSession) + "\n" + Loc.Get(Strings.Logs.ExportSessionLive)
                : Loc.Get(Strings.Logs.ExportSession);

            return new BoxEl
            {
                Key = "logs-toolbar", Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, Wrap = true, Shrink = 0f,
                Padding = new Edges4(GutterX, Spacing.L, GutterX, Spacing.S),
                Children =
                [
                    AutoSuggestBox.Create(Array.Empty<string>(), placeholder: Loc.Get(Strings.Logs.FilterPlaceholder),
                        grow: 1f, maxFillWidth: 360f, text: _search, onChange: q => _search.Value = q, onQuerySubmitted: q => _search.Value = q,
                        minHeight: 34f, cornerRadius: Radii.Control),
                    Segmented.Create(levelItems, _level),
                    ComboBox.Create(catLabels, _category, width: 180f)
                        with { Key = "logs:cat:" + _session.Value.ToString(CultureInfo.InvariantCulture) + ":" + _categories.Length.ToString(CultureInfo.InvariantCulture) },
                    new BoxEl { Grow = 1f },
                    new BoxEl
                    {
                        Direction = 0, Gap = Spacing.XS, AlignItems = FlexAlign.Center, Shrink = 0f,
                        Children =
                        [
                            ToolTip.Wrap(IconButton.Create(Icons.Copy, () => hooks.Clipboard?.SetText(LogView.CopyText(_rows))) with { Shrink = 0f },
                                Loc.Get(Strings.Logs.CopyVisible)),
                            ToolTip.Wrap(IconButton.Create(Icons.Download, ExportSession) with { Shrink = 0f }, exportTip),
                            ToolTip.Wrap(IconButton.Create(Icons.More, () => OpenMore(post)) with { Shrink = 0f, OnRealized = h => _moreAnchor = h },
                                Loc.Get(Strings.Logs.More)),
                        ],
                    },
                ],
            };
        }

        /// <summary>The "…" menu, built AT OPEN TIME so its checks and its enabled states are always the live state. Every
        /// write is posted: the menu closes and the keyed list remounts under the click.</summary>
        void OpenMore(Action<Action> post)
        {
            if (_overlay is not { } overlay) return;
            if (_menu is { IsOpen: true } open) { open.Close(); return; }

            bool newest = _newestFirst.Peek() != 0, group = _groupRepeats.Peek() != 0, wrap = _wrap.Peek() != 0;
            bool verbose = LogCapturePolicy.IsVerbose(Log.MinLevel);
            bool canClear = LogView.CanClear(_session.Peek());
            var items = new List<MenuFlyoutItem>(8)
            {
                MenuFlyoutItem.Toggle(Loc.Get(Strings.Logs.NewestFirst), newest, () => post(() => { _newestFirst.Value = newest ? 0 : 1; _expandedSeq.Value = -1; })),
                MenuFlyoutItem.Toggle(Loc.Get(Strings.Logs.GroupRepeats), group, () => post(() => { _groupRepeats.Value = group ? 0 : 1; _expandedSeq.Value = -1; })),
                MenuFlyoutItem.Toggle(Loc.Get(Strings.Logs.WrapLines), wrap, () => post(() => _wrap.Value = wrap ? 0 : 1)),
                MenuFlyoutItem.Toggle(Loc.Get(Strings.Logs.Verbose), verbose, () => post(() =>
                {
                    LogCapturePolicy.SetVerbose(Platform.Settings, !verbose);
                    _refresh.Value = _refresh.Peek() + 1;
                })),
                MenuFlyoutItem.Separator,
                new(Loc.Get(Strings.Logs.ClearView), Icons.ClearText, canClear, () => Controls.Confirm(overlay,
                    Loc.Get(Strings.Logs.ClearView), Loc.Get(Strings.Logs.ClearViewBody), Loc.Get(Strings.Logs.ClearView),
                    () => { Log.ClearRing(); _refresh.Value = _refresh.Peek() + 1; })),
                new(Loc.Get(Strings.Report.ThisSession), Icons.Attention, true, () => Feedback.Open(Feedback.ReportKind.Bug,
                    new Feedback.ReportPrefill(PastSessionId: SelectedPastSession() is { } s ? WaveeLogSessions.KeyOf(s) : null))),
            };
            // Same anchored-flyout shape as Shell.UI.cs's history menu and Settings.UI.Playback.cs's manager menu.
            var handle = overlay.Open(() => _moreAnchor, () => MenuFlyout.Create(items, () => _menu?.Close()),
                FlyoutPlacement.BottomEdgeAlignedRight,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false });
            _menu = handle;
            handle.ClosedAction = () => { if (ReferenceEquals(_menu, handle)) _menu = null; };
        }

        // ── the sessions: discovery, loading, the picker's items ────────────────────────────────────────────────────

        /// <summary>One off-thread walk of the log folder. NEVER latches: the busy flag clears in a <c>finally</c> inside the
        /// posted continuation AND when the post itself fails, a thrown walk is a caption ("couldn't read the log folder")
        /// rather than a swallowed fault, and every walk logs one <c>log.sessions.listed</c> line (G4).</summary>
        void RefreshSessions(Action<Action> post, bool force = false)
        {
            if (_sessionsBusy || (!force && _sessions is not null)) return;
            _sessionsBusy = true;
            string? basePath = Log.BasePath;   // the BASE (wavee.log): the dated live file would glob one day's rolls
            int pid = Environment.ProcessId;
            _ = Task.Run(() =>
            {
                long started = System.Diagnostics.Stopwatch.GetTimestamp();
                WaveeLogSessions.WalkResult? walk = null;
                Exception? fault = null;
                try { walk = WaveeLogSessions.ListPastSessions(basePath, pid); }
                catch (Exception ex) { fault = ex; }
                long ms = (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                LogWalk(walk, fault, ms);
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

        static void LogWalk(WaveeLogSessions.WalkResult? walk, Exception? fault, long elapsedMs)
        {
            var error = fault ?? walk?.Error;
            if (walk is null)
            {
                Log.Event(WaveeLogLevel.Warning, "log", "sessions.listed", "past sessions could not be listed", null, elapsedMs, error);
                return;
            }
            var level = error is null ? WaveeLogLevel.Info : WaveeLogLevel.Warning;
            Log.Event(level, "log", "sessions.listed", error is null ? "past sessions listed" : "past sessions listed with an error", null, elapsedMs, error,
                WaveeLogField.Of("files", walk.FilesSeen), WaveeLogField.Of("lines", walk.LinesSeen), WaveeLogField.Of("sessions", walk.Sessions.Count));
        }

        /// <summary>UI thread. The selection follows the session's KEY across the re-list (indices shift when a run appears
        /// or is pruned), the combo remounts through <see cref="_sessionsRev"/> so its labels (the live uptime) are fresh,
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
                _session.SetIfChanged(LogsPage.Rules.SelectionAfterRefresh(keep, keys));
            }
            _sessionsRev.Value = _sessionsRev.Peek() + 1;
        }

        void EnsureSessionLoaded(Action<Action> post)
        {
            int sel = _session.Peek();
            if (sel == 0 || _sessionLoadBusy || _sessions is not { } sessions || sel - 1 >= sessions.Count) return;
            var info = sessions[sel - 1];
            string key = LoadKey(info);
            if (string.Equals(_loadedKey, key, StringComparison.Ordinal)) return;
            _sessionLoadBusy = true;
            _ = Task.Run(() =>
            {
                WaveeLogEntry[]? loaded = null;
                try { loaded = WaveeLogSessions.LoadSession(info); }
                catch (Exception ex) { Log.Warn("log", "a past session could not be read", ex); }
                post(() =>
                {
                    _sessionEntries = loaded ?? [];
                    _loadedKey = key;
                    _sessionLoadBusy = false;
                    _refresh.Value = _refresh.Peek() + 1;
                    EnsureSessionLoaded(post);   // the selection may have moved while this one loaded
                });
            });
        }

        /// <summary>The picker's labels: this run first (with its live uptime), then every past run newest first.</summary>
        string[] SessionLabels()
        {
            int n = 1 + (_sessions?.Count ?? 0);
            var labels = new string[n];
            labels[0] = Strings.Logs.ThisSession(Environment.ProcessId, LogView.Uptime(TimeSpan.FromMilliseconds(Log.SinceStartMs)));
            if (_sessions is { } list)
                for (int i = 0; i < list.Count; i++)
                    labels[i + 1] = LogView.SessionLabel(list[i].StartUnixMs, list[i].Pid, list[i].EntryCount, LocalOffset);
            return labels;
        }

        WaveeLogSessions.Info? SelectedPastSession()
        {
            int sel = _session.Peek();
            return sel == 0 || _sessions is not { } sessions || sel - 1 >= sessions.Count ? null : sessions[sel - 1];
        }

        /// <summary>Live: the WHOLE ring in the file-line shape (with <c>t=</c>), independent of every filter — "Copy
        /// visible" is the filtered one. A past session exports its raw lines.</summary>
        void ExportSession()
        {
            bool live = _session.Peek() == 0;
            var past = live ? null : SelectedPastSession();
            if (!live && past is null) return;
            // The Save As dialog runs on its own thread (Pickers, #155); the export happens when it answers, UI thread.
            Pickers.Pick(
                PickerRequest.Save(Loc.Get(Strings.Logs.ExportSession),
                    live ? "wavee-session-live.txt" : "wavee-session-" + WaveeLogSessions.KeyOf(past!) + ".txt",
                    (Loc.Get(Strings.Logs.ExportLogText), "*.txt"), (Loc.Get(Strings.Logs.ExportAllFiles), "*.*")),
                done: path =>
                {
                    if (path is null) return;
                    try
                    {
                        if (live) File.WriteAllText(path, LogView.ExportText(Log.Snapshot()));
                        else WaveeLogSessions.ExportSessionToFile(past!, path);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn("log", "the session export failed", ex); }
                },
                failed: ex => Log.Warn("log", "the export dialog failed", ex));
        }

        // ── the list card: column header · body · footer ────────────────────────────────────────────────────────────

        /// <summary>The same padding, gap and column widths as <see cref="LogRow"/>; only Time is interactive (flips the sort).</summary>
        Element ColumnHeader(Action<Action> post)
        {
            bool newestFirst = _newestFirst.Value != 0;
            return new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, Height = ColumnHeaderHeight, Shrink = 0f,
                Padding = new Edges4(Spacing.M, 0f, Spacing.M, 0f),
                Children =
                [
                    new BoxEl { Width = ChevronSlot, Shrink = 0f },
                    new BoxEl { Width = DotSlot, Shrink = 0f },
                    TimeHeader(newestFirst, post),
                    HeaderLabel(Loc.Get(Strings.Logs.ColLevel)) with { Width = LevelColumn, Shrink = 0f },
                    HeaderLabel(Loc.Get(Strings.Logs.ColCategory)) with { Width = CategoryColumn, Shrink = 0f },
                    HeaderLabel(Loc.Get(Strings.Logs.ColMessage)) with { Grow = 1f, MinWidth = 0f },
                ],
            };
        }

        static TextEl HeaderLabel(string text) => Design.Type.MicroMeta(text) with { Weight = 600, Color = Tok.TextTertiary };

        /// <summary>The sortable Time header: the arrow shows where the NEWEST row sits. A click flips the order.</summary>
        Element TimeHeader(bool newestFirst, Action<Action> post) => new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = 4f, Width = TimeColumn, Shrink = 0f,
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
            OnClick = () => post(() => { _newestFirst.Value = newestFirst ? 0 : 1; _expandedSeq.Value = -1; }),
            Children =
            [
                HeaderLabel(Loc.Get(Strings.Logs.ColTime)) with { HoverColor = Tok.TextPrimary },
                new TextEl(newestFirst ? Icons.ChevronDown : Icons.ChevronUp)
                    { Size = 10f, FontFamily = Theme.IconFont, Color = Tok.TextTertiary, HoverColor = Tok.TextPrimary, Shrink = 0f },
            ],
        };

        Element LogBody(WaveeLogEntry[]? entries, LogViewResult result, LogViewQuery query, MeasuredStackVirtualLayout layout)
        {
            if (entries is null)
                return new BoxEl
                {
                    Grow = 1f, Direction = 1, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Gap = Spacing.M,
                    Children = [ProgressRing.Indeterminate(), new TextEl(Loc.Get(Strings.Logs.LoadingSession)) { Size = 12f, Color = Tok.TextSecondary }],
                };
            if (result.Shown == 0)
                return new BoxEl
                {
                    Grow = 1f, Direction = 1, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Gap = Spacing.M,
                    Padding = new Edges4(0f, 64f, 0f, 64f),
                    Children = [FluentGpu.Dsl.Ui.Icon(Icons.Search, 36f, Tok.TextTertiary), Design.Type.PageHero(Loc.Get(Strings.Logs.EmptyFilter))],
                };

            // The list's ItemCount/template freeze at mount: the visible SET changing remounts it; ScrollKey keeps the offset.
            string scrollKey = "logs:scroll:" + _session.Value.ToString(CultureInfo.InvariantCulture);
            var rows = result.Rows;
            return new BoxEl
            {
                Key = "logs:list:" + LogView.RemountKey(_session.Value, query, result.Shown),
                Grow = 1f, Shrink = 1f, MinHeight = 0f,
                Children =
                [
                    ItemsView.Create(rows.Length, i => LogRow(rows[i]), RepeatLayout.Measured(layout), new ListOptions
                    {
                        SelectionMode = ItemsSelectionMode.None, Controller = _listCtrl, Selector = SelectorVisual.None,
                        KeyOf = i => scrollKey + ":" + rows[i].Entry.Sequence.ToString(CultureInfo.InvariantCulture),
                        IsItemInvokedEnabled = true, OnInvoked = i => ToggleExpand(rows[i].Entry.Sequence, rows), Grow = 1f,
                        Scroll = new ScrollOptions { ScrollKey = scrollKey },
                    }),
                ],
            };
        }

        void ToggleExpand(long seq, LogViewRow[] rows)
        {
            _expandedSeq.Value = _expandedSeq.Peek() == seq ? -1 : seq;
            int idx = LogView.IndexOfSequence(rows, seq);
            if (idx >= 0) _listCtrl.StartBringItemIntoView(idx, alignmentRatio: 0f);
        }

        /// <summary>36 DIP, fixed columns (<see cref="ColumnHeader"/> mirrors them): chevron 12 · dot 6 · time 92 · pill
        /// 58 × 22 · category 96 · message · ×N.</summary>
        Element LogRow(LogViewRow row)
        {
            var e = row.Entry;
            long seq = e.Sequence;
            bool expanded = _expandedSeq.Value == seq;
            bool wrapAll = expanded || _wrap.Value != 0;
            var line = new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinHeight = 36f, Padding = new Edges4(Spacing.M, 0f, Spacing.M, 0f),
                Grow = 1f, Fill = expanded ? Tok.FillSubtleSecondary : ColorF.Transparent,
                Children =
                [
                    Sidebar.Chevron.Disclosure(() => _expandedSeq.Value == seq, size: ChevronSlot),
                    SeverityDot(e.Level),
                    new TextEl(LogView.FormatTime(e.UnixMs, LocalOffset)) { Size = 12f, Color = Tok.TextSecondary, FontFamily = "Cascadia Code", Width = TimeColumn, Shrink = 0f },
                    LevelPill(e.Level),
                    new TextEl(e.Category) { Size = 12f, Color = Tok.TextSecondary, FontFamily = "Cascadia Code", Width = CategoryColumn, Shrink = 0f, Trim = TextTrim.CharacterEllipsis },
                    Design.Type.DenseMeta(e.Message) with
                    {
                        Color = Tok.TextPrimary, Grow = 1f, MinWidth = 0f,
                        Wrap = wrapAll ? TextWrap.Wrap : TextWrap.NoWrap, Trim = wrapAll ? TextTrim.None : TextTrim.CharacterEllipsis, MaxLines = wrapAll ? 0 : 1,
                    },
                    row.Repeat > 1 ? RepeatBadge(row.Repeat) : new BoxEl(),
                ],
            }.Interactive(Interaction.ListRow);
            if (!expanded) return line;

            // The expanded row is a keyed WRAPPER: the line, Fields (only when non-empty), Exception (only when present),
            // and the meta line, which ALWAYS renders.
            var detail = new List<Element>(4) { line };
            string fieldText = LogView.FieldText(e.Fields);
            if (fieldText.Length > 0) detail.Add(DetailSection(Loc.Get(Strings.Logs.Fields), fieldText));
            if (e.Exception is { Length: > 0 } ex) detail.Add(DetailSection(Loc.Get(Strings.Logs.Exception), ex));
            detail.Add(Design.Type.MicroMeta(LogView.MetaLine(in e)) with { Color = Tok.TextTertiary, FontFamily = "Cascadia Code", Margin = new Edges4(44f, 0f, 0f, 0f) });
            return new BoxEl { Key = "logs:row:" + seq.ToString(CultureInfo.InvariantCulture), Direction = 1, Gap = 4f, Padding = new Edges4(0f, 0f, Spacing.S, Spacing.S), Children = detail.ToArray() };
        }

        static Element DetailSection(string caption, string text) => new BoxEl
        {
            Direction = 1, Gap = 4f, Padding = new Edges4(44f, 0f, Spacing.M, 4f),
            Children = [Design.Type.MicroMeta(caption) with { Weight = 600, Color = Tok.TextTertiary }, CodeBlock.Create(text, copyable: true, fontSize: 12f)],
        };

        /// <summary>A dot only for Warning and ≥ Error; every other level holds the 6-DIP column with a blank spacer.</summary>
        static Element SeverityDot(WaveeLogLevel level) => level switch
        {
            WaveeLogLevel.Warning => InfoBadge.Dot(InfoBadgeSeverity.Caution),
            >= WaveeLogLevel.Error => InfoBadge.Dot(InfoBadgeSeverity.Critical),
            _ => new BoxEl { Width = DotSlot, Height = DotSlot, Shrink = 0f },
        };

        static Element RepeatBadge(int repeat) => new BoxEl
        {
            Padding = new Edges4(7f, 1f, 7f, 2f), Corners = CornerRadius4.All(Radii.Full), Fill = Tok.FillSubtleSecondary,
            Children = [Design.Type.MicroMeta("×" + repeat.ToString(CultureInfo.InvariantCulture)) with { Weight = 700, Color = Tok.TextSecondary }],
        };

        static BoxEl LevelPill(WaveeLogLevel level)
        {
            var color = level switch
            {
                WaveeLogLevel.Critical or WaveeLogLevel.Error => Tok.SystemFillCritical,
                WaveeLogLevel.Warning => Tok.SystemFillCaution,
                WaveeLogLevel.Debug or WaveeLogLevel.Trace => Tok.TextTertiary,
                _ => Tok.AccentDefault,
            };
            return new BoxEl
            {
                Width = LevelColumn, Height = 22f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = CornerRadius4.All(Radii.Full),
                Fill = color with { A = 0.12f }, BorderWidth = 1f, BorderColor = color with { A = 0.38f },
                Children = [new TextEl(level.ToString().ToUpperInvariant()) { Size = 10f, Weight = 800, Color = color }],
            };
        }

        // ── the footer: shown/total + Load more + the capture caption ────────────────────────────────────────────────

        /// <summary>A past session the loader capped says so ("last 4,096 of 17,936 events · log file") instead of
        /// advertising the full count over a partial list (audit #9, G8).</summary>
        Element Footer(LogViewResult result, bool live, WaveeLogEntry[]? entries, WaveeLogSessions.Info? past)
        {
            string text;
            if (live) text = Strings.Logs.FooterLive(LogsPage.Rules.Thousands(result.Shown), LogsPage.Rules.Thousands(result.Total));
            else if (past is not null && entries is not null && entries.Length < past.EntryCount)
                text = Strings.Logs.FooterPastCapped(LogsPage.Rules.Thousands(entries.Length), LogsPage.Rules.Thousands(past.EntryCount));
            else text = Strings.Logs.FooterPast(LogsPage.Rules.Thousands(result.Shown), LogsPage.Rules.Thousands(result.Total));

            var kids = new List<Element>(3)
            {
                new TextEl(text) { Size = 12f, Color = Tok.TextSecondary, Grow = 1f, MinWidth = 0f },
            };
            // The cap stops growing at LogView.MaxRows; past it there is nothing more to load.
            if (result.Truncated && _visibleLimit.Value < LogView.MaxRows)
                kids.Add(HyperlinkButton.Create(Loc.Get(Strings.Logs.LoadMore), () => _visibleLimit.Value = LogView.NextCap(_visibleLimit.Peek())));
            var min = Log.MinLevel;
            var file = LogCapturePolicy.EffectiveFileLevel(min, Log.FileMinLevel);
            kids.Add(Design.Type.MicroMeta(Strings.Logs.CaptureCaption(LevelName(min), LevelName(file))) with { Color = Tok.TextTertiary });
            return new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, Padding = new Edges4(Spacing.L, Spacing.S, Spacing.M, Spacing.S),
                Children = kids.ToArray(),
            };
        }
    }
}
