using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using FluentGpu.WindowsApi.Dialogs;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>Settings › Logs: the full-height log viewer that replaced the old Diagnostics tab's log half
/// (<c>DiagnosticsPanel</c> — deleted by the Settings-regroup workstream; its developer switches and crash-report
/// card moved to General › Developer and About › Reports). Every filter/format decision lives in the engine-free
/// <see cref="LogView"/>/<see cref="LogCapturePolicy"/> pure classes (Diagnostics/) so it is unit-testable without
/// standing up FluentGpu — this component only wires signals, hooks and controls to them.
///
/// <para><b>Key-remount discipline.</b> The session/category <see cref="ComboBox"/>, the two capture-level combos and
/// the <see cref="CommandBar"/> are all "autonomous" child components whose interesting fields freeze at first mount
/// (<c>ComboBox.Create</c> only re-pushes its <c>IsEnabled</c> prop; <c>CommandBar</c>'s command lists are plain
/// frozen fields — see the fluentgpu skill's component-props-contract). Each is remounted with a <c>Key</c> that
/// changes exactly when the FROZEN inputs it was built from change, so the live label/selection lists never go
/// stale. A toggle/level click always runs through <see cref="UsePost"/> so the click finishes before the very
/// remount it triggers tears the clicked node down.</para>
///
/// <para><b>Field-based closures.</b> <see cref="_rows"/> and <see cref="_categories"/> are FIELDS (not render-local
/// captures) precisely so a toolbar command built by an OLDER render (the bar didn't need a remount for a plain
/// search-text edit) still reads the CURRENT filtered rows when clicked — a captured local array would go stale the
/// moment the search box changed without a bar remount.</para></summary>
sealed class LogsPanel(IAppSettings? settings = null) : Component
{
    readonly Signal<string> _search = new("");
    readonly Signal<int> _level = new((int)LogLevelBucket.All);
    readonly Signal<int> _category = new(0);
    readonly Signal<int> _session = new(0);
    readonly Signal<int> _newestFirst = new(1);
    readonly Signal<int> _groupRepeats = new(1);
    readonly Signal<int> _wrap = new(0);
    readonly Signal<int> _refresh = new(0);          // bumped by: the live poll, a session/entries load landing, Clear view
    readonly Signal<int> _visibleLimit = new(LogView.PageRows);
    readonly Signal<int> _sessionsRev = new(0);       // bumps ONLY when the session LIST (labels/subs) changes
    readonly Signal<int> _levelsRev = new(0);         // bumps whenever Verbose / Capture level / File log level change
    readonly Signal<long> _expandedSeq = new(-1);

    List<WaveeLogSessions.Info>? _sessions;
    bool _sessionsBusy;
    WaveeLogEntry[]? _sessionEntries;
    int _sessionLoaded;
    bool _sessionLoadBusy;
    // The current render's filtered/grouped rows and the categories derived from the loaded entries — FIELDS (see
    // the class doc's "field-based closures" note), refreshed at the top of every Render() before anything reads them.
    LogViewRow[] _rows = [];
    string[] _categories = [];
    readonly ItemsViewController _listCtrl = new();
    IOverlayService? _overlay;

    static TimeSpan LocalOffset => TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.UtcNow);

    public override Element Render()
    {
        var hooks = UseContext(InputHooks.Current);
        var post = UsePost();
        var lastVersion = UseRef(-1L);
        _overlay = UseContext(Overlay.Service);

        UseEffect(() => RefreshSessions(post), DepKey.Empty);

        UseSignalEffect(() =>
        {
            _ = _session.Value;
            _expandedSeq.Value = -1;
            _visibleLimit.Value = LogView.PageRows;
            EnsureSessionLoaded(post);
        });

        // Live-log tail poll (session 0 only): auto-pauses while parked/minimized. Bumps _refresh only when the
        // log's own version moved, so a quiet session never re-renders for nothing.
        UseInterval(() =>
        {
            long v = WaveeLog.Instance.Version;
            if (v == lastVersion.Value) return;
            lastVersion.Value = v;
            _refresh.Value = _refresh.Peek() + 1;
        }, 750f, enabled: _session.Value == 0);

        // Hoisted unconditionally so hook order stays stable across LogBody's loading/empty/rows early-outs.
        var logLayout = UseMemo(() => new MeasuredStackVirtualLayout(estimatedExtent: 36f), DepKey.Empty);

        _ = _refresh.Value;
        _ = _expandedSeq.Value;
        _ = _search.Value;
        _ = _level.Value;
        _ = _category.Value;
        _ = _newestFirst.Value;
        _ = _groupRepeats.Value;
        _ = _wrap.Value;
        _ = _visibleLimit.Value;
        _ = _levelsRev.Value;

        bool live = _session.Value == 0;
        WaveeLogEntry[]? entries = live ? WaveeLog.Instance.Snapshot()
            : _sessionLoaded == _session.Value ? _sessionEntries : null;

        _categories = entries is null ? [] : LogView.Categories(entries);

        // The category clamp lives in an effect (never in render): a category the loaded entries no longer contain
        // (a new session, or the live ring aging the last entry of a category out) resets the filter to "All"
        // instead of silently pinning an out-of-range index.
        UseEffect(() =>
        {
            if (_category.Peek() > _categories.Length) _category.Value = 0;
        }, _categories.Length);

        var query = new LogViewQuery(
            (LogLevelBucket)_level.Value,
            CurrentCategory(),
            _search.Value,
            _newestFirst.Value != 0,
            _groupRepeats.Value != 0,
            _visibleLimit.Value);
        var result = entries is null ? LogViewResult.Empty : LogView.Build(entries, query);
        _rows = result.Rows;

        return new BoxEl
        {
            Grow = 1f, Shrink = 1f, MinHeight = 0f, Direction = 1, Gap = Spacing.M,
            Children =
            [
                HeaderRow(hooks, post, live),
                FilterRow(result, post),
                new BoxEl
                {
                    Grow = 1f, Shrink = 1f, MinHeight = 0f, Direction = 1,
                    Corners = CornerRadius4.All(Radii.Card), Fill = Tok.FillCardSecondary,
                    BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, ClipToBounds = true,
                    Children =
                    [
                        LogBody(entries, result, logLayout),
                        Divider(),
                        Footer(result, live),
                    ],
                },
            ],
        };
    }

    // ── header: session picker + command bar ─────────────────────────────────────────────────────────────────────

    Element HeaderRow(InputHooks hooks, Action<Action> post, bool live)
    {
        var (labels, subs) = BuildSessionItems();
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, MinHeight = 48f, Shrink = 0f,
            Children =
            [
                ComboBox.Create(labels, _session, width: 320f, itemDescriptions: subs,
                    onChange: _ => _expandedSeq.Value = -1) with { Key = "logs:session:" + _sessionsRev.Value },
                new BoxEl { Grow = 1f },
                CommandBar.Create(PrimaryCommands(hooks, post, live), SecondaryCommands(post))
                    with { Key = "logs:bar:" + BarKey(live) },
            ],
        };
    }

    string BarKey(bool live) =>
        (live ? "L" : "P") + ":" + _newestFirst.Value + ":" + _groupRepeats.Value + ":" + _wrap.Value + ":" + _levelsRev.Value;

    IReadOnlyList<AppBarCommand> PrimaryCommands(InputHooks hooks, Action<Action> post, bool live)
    {
        bool newestFirst = _newestFirst.Value != 0;
        bool groupRepeats = _groupRepeats.Value != 0;
        bool wrap = _wrap.Value != 0;
        bool verbose = LogCapturePolicy.IsVerbose(WaveeLog.Instance.MinLevel);
        return
        [
            new(Icons.Refresh, Loc.Get(Strings.Settings.Diagnostics.Refresh), () =>
            {
                RefreshSessions(post, force: true);
                if (live) _refresh.Value = _refresh.Peek() + 1;
            }),
            new(Icons.Copy, Loc.Get(Strings.Settings.Diagnostics.CopyVisible),
                () => hooks.Clipboard?.SetText(LogView.CopyText(_rows))),
            new(Icons.Download, Loc.Get(Strings.Settings.Diagnostics.ExportSession), ExportSession),
            new(Icons.Folder, Loc.Get(Strings.Settings.Diagnostics.OpenLogFolder),
                () => SettingsShared.OpenFolder(Path.GetDirectoryName(WaveeLog.Instance.FilePath ?? "") ?? SettingsShared.AppDataRoot)),
            new(Icons.ClearText, Loc.Get(Strings.Settings.Diagnostics.ClearView),
                () => SettingsShared.Confirm(_overlay,
                    Loc.Get(Strings.Settings.Diagnostics.ClearView),
                    Loc.Get(Strings.Settings.Diagnostics.ClearViewBody),
                    Loc.Get(Strings.Settings.Diagnostics.ClearView),
                    () => { WaveeLog.Instance.ClearRing(); _refresh.Value = _refresh.Peek() + 1; }),
                Enabled: live),
            AppBarCommand.Separator,
            new(Icons.Sort, Loc.Get(Strings.Settings.Diagnostics.NewestFirst),
                () => post(() => { _newestFirst.Value = newestFirst ? 0 : 1; _expandedSeq.Value = -1; }),
                Kind: AppBarCommandKind.ToggleButton, IsChecked: newestFirst),
            new(Icons.List, Loc.Get(Strings.Settings.Diagnostics.GroupRepeats),
                () => post(() => { _groupRepeats.Value = groupRepeats ? 0 : 1; _expandedSeq.Value = -1; }),
                Kind: AppBarCommandKind.ToggleButton, IsChecked: groupRepeats),
            new(Icons.Code, Loc.Get(Strings.Settings.Diagnostics.Verbose),
                () => post(() => ApplyVerbose(!verbose)),
                Kind: AppBarCommandKind.ToggleButton, IsChecked: verbose),
        ];
    }

    IReadOnlyList<AppBarCommand> SecondaryCommands(Action<Action> post)
    {
        bool wrap = _wrap.Value != 0;
        return
        [
            new(default, Loc.Get(Strings.Settings.Diagnostics.WrapLines),
                () => post(() => _wrap.Value = wrap ? 0 : 1),
                Kind: AppBarCommandKind.ToggleButton, IsChecked: wrap),
            AppBarCommand.Separator,
            new(Icons.Attention, Loc.Get(Strings.Report.ThisSession),
                () => ReportRequests.Open(ReportKind.Bug, new ReportPrefill(PastSession: SelectedPastSession()))),
        ];
    }

    // ── filter row: search + level segments + category + capture/file level ────────────────────────────────────────
    //
    // Capture level / File log level are plain ComboBoxes here rather than the cascading Flyout radios the plan
    // wireframe sketches in the overflow: AppBarCommand.Flyout (the cascading sub-menu) is only wired up inside
    // CommandBarFlyout's own overflow builder (CommandBarFlyoutBody.BuildOverflow, CommandBarFlyout.cs:961/1066) —
    // plain CommandBar's overflow (CommandBar.cs BuildOverflow) renders AppBarButton.CreateOverflow rows with no
    // Flyout support at all, so a cascading radio sub-menu there is simply inert. A ComboBox is the same control the
    // OLD DiagnosticsPanel already used for these two knobs and needs no engine change.
    Element FilterRow(LogViewResult result, Action<Action> post)
    {
        var catLabels = new string[_categories.Length + 1];
        catLabels[0] = Loc.Get(Strings.Settings.Diagnostics.AllCategories);
        Array.Copy(_categories, 0, catLabels, 1, _categories.Length);

        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, Wrap = true, MinHeight = 40f, Shrink = 0f,
            Children =
            [
                AutoSuggestBox.Create(Array.Empty<string>(),
                    placeholder: Loc.Get(Strings.Settings.Diagnostics.FilterPlaceholder),
                    grow: 1f, text: _search, onChange: q => _search.Value = q, onQuerySubmitted: q => _search.Value = q,
                    minHeight: 34f, cornerRadius: Radii.Control),
                Segmented.Create(LevelItems(), _level),
                result.WarningCount > 0
                    ? ClickableBadge(InfoBadge.Count(result.WarningCount, InfoBadgeSeverity.Caution), () => _level.Value = (int)LogLevelBucket.Warnings)
                    : new BoxEl(),
                result.ErrorCount > 0
                    ? ClickableBadge(InfoBadge.Count(result.ErrorCount, InfoBadgeSeverity.Critical), () => _level.Value = (int)LogLevelBucket.Errors)
                    : new BoxEl(),
                new BoxEl { Grow = 1f },
                ComboBox.Create(catLabels, _category, width: 180f)
                    with { Key = "logs:cat:" + _session.Value + ":" + _categories.Length },
                ComboBox.Create(LogView.LevelNames, new Signal<int>(Math.Clamp((int)WaveeLog.Instance.MinLevel, 0, 4)),
                    width: 132f, header: Loc.Get(Strings.Settings.Diagnostics.CaptureLevel),
                    onChange: i => post(() => ApplyMinLevel((WaveeLogLevel)Math.Clamp(i, 0, 4))))
                    with { Key = "logs:level:" + _levelsRev.Value },
                ComboBox.Create(LogView.LevelNames, new Signal<int>(Math.Clamp((int)WaveeLog.Instance.FileMinLevel, 0, 4)),
                    width: 132f, header: Loc.Get(Strings.Settings.Diagnostics.FileLevel),
                    onChange: i => post(() => ApplyFileLevel((WaveeLogLevel)Math.Clamp(i, 0, 4))))
                    with { Key = "logs:filelevel:" + _levelsRev.Value },
            ],
        };
    }

    static SegmentedItem[] LevelItems() =>
    [
        new(Loc.Get(Strings.Settings.Diagnostics.LevelAll)),
        new(Loc.Get(Strings.Settings.Diagnostics.LevelInfo)),
        new(Loc.Get(Strings.Settings.Diagnostics.LevelWarnings)),
        new(Loc.Get(Strings.Settings.Diagnostics.LevelErrors)),
    ];

    static Element ClickableBadge(BoxEl badge, Action onClick) =>
        badge with { Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = onClick };

    // ── level application: the panel's only writers, mirroring WaveeLog + the persisted setting together ───────────

    void ApplyVerbose(bool on)
    {
        LogCapturePolicy.SetVerbose(WaveeLog.Instance, settings, on, LogCapturePolicy.BuildDefaultMinLevel);
        _levelsRev.Value = _levelsRev.Peek() + 1;
    }

    void ApplyMinLevel(WaveeLogLevel level)
    {
        LogCapturePolicy.SetMinLevel(WaveeLog.Instance, settings, level, LogCapturePolicy.BuildDefaultMinLevel);
        _levelsRev.Value = _levelsRev.Peek() + 1;
    }

    void ApplyFileLevel(WaveeLogLevel level)
    {
        LogCapturePolicy.SetFileLevel(WaveeLog.Instance, settings, level);
        _levelsRev.Value = _levelsRev.Peek() + 1;
    }

    string? CurrentCategory()
    {
        int idx = _category.Value;
        return idx <= 0 || idx > _categories.Length ? null : _categories[idx - 1];
    }

    // ── sessions: discovery, loading, the picker's items ─────────────────────────────────────────────────────────

    void RefreshSessions(Action<Action> post, bool force = false)
    {
        if (!force && (_sessions is not null || _sessionsBusy)) return;
        _sessionsBusy = true;
        // BasePath, not FilePath: discovery globs "<root>-*.log" off this path, so the DATED live file would narrow
        // the glob to one day's rolls. The base (wavee.log) yields the whole wavee-*.log set.
        string? file = WaveeLog.Instance.BasePath;
        int pid = Environment.ProcessId;
        _ = Task.Run(() =>
        {
            var list = WaveeLogSessions.ListPastSessions(file, pid);
            post(() =>
            {
                _sessions = list;
                _sessionsBusy = false;
                if (_session.Peek() > list.Count) _session.Value = 0;
                _sessionLoaded = 0;
                _sessionEntries = null;
                _sessionsRev.Value = _sessionsRev.Peek() + 1;
            });
        });
    }

    void EnsureSessionLoaded(Action<Action> post)
    {
        int sel = _session.Peek();
        if (sel == 0 || _sessionLoadBusy || _sessions is not { } sessions || _sessionLoaded == sel) return;
        if (sel - 1 >= sessions.Count) return;
        var info = sessions[sel - 1];
        _sessionLoadBusy = true;
        _ = Task.Run(() =>
        {
            var entries = WaveeLogSessions.LoadSession(info);
            post(() =>
            {
                _sessionEntries = entries;
                _sessionLoaded = sel;
                _sessionLoadBusy = false;
                _refresh.Value = _refresh.Peek() + 1;
                EnsureSessionLoaded(post);
            });
        });
    }

    (string[] labels, string[] subs) BuildSessionItems()
    {
        int n = 1 + (_sessions?.Count ?? 0);
        var labels = new string[n];
        var subs = new string[n];
        labels[0] = Loc.Get(Strings.Settings.Diagnostics.CurrentRun);
        subs[0] = LiveSessionSubtitle();
        if (_sessions is { } list)
            for (int i = 0; i < list.Count; i++)
            {
                var s = list[i];
                labels[i + 1] = LogView.SessionLabel(s.StartUnixMs, s.Pid, s.EntryCount, LocalOffset);
                subs[i + 1] = Strings.Settings.Diagnostics.SessionEvents(s.EntryCount);
            }
        return (labels, subs);
    }

    static string LiveSessionSubtitle()
    {
        DateTimeOffset started;
        try { started = System.Diagnostics.Process.GetCurrentProcess().StartTime; }
        catch { started = DateTimeOffset.Now; }
        return Strings.Settings.Diagnostics.RunningFor(Environment.ProcessId, LogView.Uptime(DateTimeOffset.Now - started));
    }

    /// <summary>The past session the picker currently has selected, or null when the live ring (index 0) is
    /// selected — <c>ReportComposer.Compose</c> reads null as "use the live ring".</summary>
    WaveeLogSessions.Info? SelectedPastSession()
    {
        int sel = _session.Peek();
        if (sel == 0 || _sessions is not { } sessions || sel - 1 >= sessions.Count) return null;
        return sessions[sel - 1];
    }

    void ExportSession()
    {
        bool live = _session.Peek() == 0;
        string defaultName = live ? "wavee-session-live.txt" : "wavee-session-" + _session.Peek() + ".txt";
        string? path = FilePicker.SaveFile(FluentApp.WindowHandle,
            Loc.Get(Strings.Settings.Diagnostics.ExportSession), defaultName,
            new("Log text", "*.txt"), new("All files", "*.*"));
        if (path is null) return;
        try
        {
            if (live) File.WriteAllText(path, LogView.CopyText(_rows));
            else if (_sessions is { } sessions && _session.Peek() - 1 < sessions.Count)
                WaveeLogSessions.ExportSessionToFile(sessions[_session.Peek() - 1], path);
        }
        catch { /* best-effort */ }
    }

    // ── the log body: loading / empty / rows ─────────────────────────────────────────────────────────────────────

    Element LogBody(WaveeLogEntry[]? entries, LogViewResult result, MeasuredStackVirtualLayout layout)
    {
        if (entries is null)
        {
            return new BoxEl
            {
                Grow = 1f, Direction = 1, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Gap = Spacing.M,
                Children =
                [
                    ProgressRing.Indeterminate(),
                    new TextEl(Loc.Get(Strings.Settings.Diagnostics.LoadingSession)) { Size = 12f, Color = Tok.TextSecondary },
                ],
            };
        }

        if (result.Shown == 0)
        {
            return new BoxEl
            {
                Grow = 1f, Direction = 1, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Gap = Spacing.M, Padding = new Edges4(0, 64, 0, 64),
                Children =
                [
                    Icon(Icons.Search, 36f, Tok.TextTertiary),
                    WaveeType.PageHero(Loc.Get(Strings.Settings.Diagnostics.EmptyFilter)),
                ],
            };
        }

        // ItemsView is an autonomous component: its ItemCount/ItemTemplate freeze at first mount. The filter/search/
        // level/category and the live-growing row count are NOT carried reactively, so the list is REMOUNTED whenever
        // the visible SET changes (LogView.RemountKey). scrollKey restores the offset across that remount.
        string scrollKey = "logs:scroll:" + _session.Value;
        string remountKey = LogView.RemountKey(_session.Value,
            new LogViewQuery((LogLevelBucket)_level.Value, CurrentCategory(), _search.Value,
                _newestFirst.Value != 0, _groupRepeats.Value != 0, _visibleLimit.Value),
            result.Shown);

        return new BoxEl
        {
            Key = "logs:list:" + remountKey,
            Grow = 1f, Shrink = 1f, MinHeight = 0f,
            Children =
            [
                ItemsView.Create(
                    result.Rows.Length, i => LogRow(result.Rows[i]),
                    RepeatLayout.Measured(layout),
                    new ListOptions
                    {
                        SelectionMode = ItemsSelectionMode.None,
                        Controller = _listCtrl,
                        Selector = SelectorVisual.None,
                        KeyOf = i => scrollKey + ":" + result.Rows[i].Entry.Sequence,
                        IsItemInvokedEnabled = true,
                        OnInvoked = i => ToggleExpand(result.Rows[i].Entry.Sequence, result.Rows),
                        Grow = 1f,
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

    Element LogRow(LogViewRow row)
    {
        var e = row.Entry;
        bool expanded = _expandedSeq.Value == e.Sequence;
        bool wrapAll = expanded || _wrap.Value != 0;

        var line = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S,
            MinHeight = 36f, Padding = new Edges4(Spacing.M, 0f, Spacing.M, 0f), Grow = 1f,
            Fill = expanded ? Tok.FillSubtleSecondary : ColorF.Transparent,
            Children =
            [
                SidebarChevron.Disclosure(() => _expandedSeq.Value == e.Sequence, size: 12f),
                SeverityDot(e.Level),
                new TextEl(LogView.FormatTime(e.UnixMs, LocalOffset))
                    { Size = 12f, Color = Tok.TextSecondary, FontFamily = "Cascadia Code", Width = 92f, Shrink = 0f },
                LevelPill(e.Level),
                new TextEl(e.Category)
                {
                    Size = 12f, Color = Tok.TextSecondary, FontFamily = "Cascadia Code", Width = 96f, Shrink = 0f,
                    Trim = TextTrim.CharacterEllipsis,
                },
                new TextEl(e.Message)
                {
                    Size = 13f, Color = Tok.TextPrimary, Grow = 1f,
                    Wrap = wrapAll ? TextWrap.Wrap : TextWrap.NoWrap,
                    Trim = wrapAll ? TextTrim.None : TextTrim.CharacterEllipsis,
                    MaxLines = wrapAll ? 0 : 1,
                },
                row.Repeat > 1 ? RepeatBadge(row.Repeat) : new BoxEl(),
            ],
        }.Interactive(Interaction.ListRow);

        if (!expanded) return line;

        var detail = new List<Element>(4) { line };
        string fieldText = LogView.FieldText(e.Fields);
        if (fieldText.Length > 0) detail.Add(DetailSection(Loc.Get(Strings.Settings.Diagnostics.Fields), fieldText));
        if (e.Exception is { Length: > 0 } ex) detail.Add(DetailSection(Loc.Get(Strings.Settings.Diagnostics.Exception), ex));
        detail.Add(new TextEl(LogView.MetaLine(e))
            { Size = 11f, Color = Tok.TextTertiary, FontFamily = "Cascadia Code", Margin = new Edges4(44f, 0f, 0f, 0f) });
        return new BoxEl { Key = "logs:row:" + e.Sequence, Direction = 1, Gap = 4f, Padding = new Edges4(0, 0, Spacing.S, Spacing.S), Children = detail.ToArray() };
    }

    static Element DetailSection(string caption, string text) => new BoxEl
    {
        Direction = 1, Gap = 4f, Padding = new Edges4(44f, 0f, Spacing.M, 4f),
        Children =
        [
            new TextEl(caption) { Size = 11f, Weight = 600, Color = Tok.TextTertiary },
            CodeBlock.Create(text, copyable: true, fontSize: 12f),
        ],
    };

    static Element SeverityDot(WaveeLogLevel level) => level switch
    {
        WaveeLogLevel.Warning => InfoBadge.Dot(InfoBadgeSeverity.Caution),
        >= WaveeLogLevel.Error => InfoBadge.Dot(InfoBadgeSeverity.Critical),
        _ => new BoxEl { Width = 6f, Height = 6f, Shrink = 0f },
    };

    static Element RepeatBadge(int repeat) => new BoxEl
    {
        Padding = new Edges4(7f, 1f, 7f, 2f), Corners = CornerRadius4.All(Radii.Full),
        Fill = Tok.FillSubtleSecondary,
        Children = [new TextEl("×" + repeat.ToString(CultureInfo.InvariantCulture)) { Size = 10.5f, Weight = 700, Color = Tok.TextSecondary }],
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
            Width = 58f, Height = 22f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(Radii.Full),
            Fill = color with { A = 0.12f }, BorderWidth = 1f, BorderColor = color with { A = 0.38f },
            Children = [new TextEl(level.ToString().ToUpperInvariant()) { Size = 10f, Weight = 800, Color = color }],
        };
    }

    // ── footer: shown/total + Load more + the capture caption ───────────────────────────────────────────────────

    Element Footer(LogViewResult result, bool live)
    {
        var kids = new List<Element>(3)
        {
            new TextEl(live
                ? Strings.Settings.Diagnostics.FooterLive(result.Shown, result.Total)
                : Strings.Settings.Diagnostics.FooterPast(result.Shown, result.Total))
            { Size = 12f, Color = Tok.TextSecondary, Grow = 1f },
        };
        if (result.Truncated)
            kids.Add(HyperlinkButton.Create(Loc.Get(Strings.Settings.Diagnostics.LoadMore),
                () => _visibleLimit.Value = Math.Min(LogView.MaxRows, _visibleLimit.Peek() + LogView.PageRows)));

        var min = WaveeLog.Instance.MinLevel;
        var file = LogCapturePolicy.EffectiveFileLevel(min, WaveeLog.Instance.FileMinLevel);
        kids.Add(new TextEl(Strings.Settings.Diagnostics.CaptureCaption(LogView.LevelNames[(int)min], LogView.LevelNames[(int)file]))
            { Size = 11f, Color = Tok.TextTertiary });

        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M,
            Padding = new Edges4(Spacing.L, Spacing.S, Spacing.M, Spacing.S),
            Children = kids.ToArray(),
        };
    }
}
