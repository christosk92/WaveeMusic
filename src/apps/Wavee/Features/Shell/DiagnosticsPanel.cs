using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
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
using Wavee.Backend.Audio;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>Settings → Diagnostics: session log viewer with accordion rows and a two-row toolbar.</summary>
sealed class DiagnosticsPanel(IAppSettings? settings = null) : Component
{
    const int MaxVisibleRows = 2000;
    static readonly string[] s_levelLabels = ["All", "Info+", "Warnings", "Errors"];
    // The runtime log-level dropdown items (Trace..Error). Index == (int)WaveeLogLevel; Critical is not user-selectable.
    static readonly string[] s_logLevels = ["Trace", "Debug", "Info", "Warning", "Error"];
    static readonly string[] s_categories =
    [
        "All", "app", "audio", "auth", "connect", "crash", "dealer", "engine", "log", "lyrics",
        "metadata", "mutation", "notifications", "playback", "probe", "social", "spclient", "sync",
        "telemetry", "ui",
    ];

    readonly Signal<string> _search = new("");
    readonly Signal<int> _level = new(0);
    readonly Signal<int> _category = new(0);
    readonly Signal<int> _newestFirst = new(1);
    readonly Signal<int> _groupRepeats = new(1);
    readonly Signal<int> _refresh = new(0);
    readonly Signal<int> _visibleLimit = new(500);
    readonly Signal<long> _expandedSeq = new(-1);
    readonly Signal<int> _session = new(0);
    readonly Signal<int> _diagVersion = new(0);
    // Runtime log-level dropdowns — seeded from the live logger, clamped to the selectable Trace..Error range.
    readonly Signal<int> _minLevel = new(Math.Clamp((int)WaveeLog.Instance.MinLevel, 0, 4));
    readonly Signal<int> _fileLevel = new(Math.Clamp((int)WaveeLog.Instance.FileMinLevel, 0, 4));

    List<WaveeLogSessions.Info>? _sessions;
    bool _sessionsBusy;
    WaveeLogEntry[]? _sessionEntries;
    int _sessionLoaded;
    bool _sessionLoadBusy;
    readonly ItemsViewController _listCtrl = new();
    IOverlayService? _overlay;
    // The notification bridge the developer-mode update simulator injects snapshots into. Captured UNCONDITIONALLY in
    // Render (a context read behind the developer-mode branch would be a conditional hook).
    NotificationCenterBridge? _nc;

    readonly record struct LogRowData(WaveeLogEntry Entry, int Repeat);

    public override Element Render()
    {
        var hooks = UseContext(InputHooks.Current);
        var post = UsePost();
        var lastVersion = UseRef(-1L);
        _overlay = UseContext(Overlay.Service);
        _nc = UseContext(NotificationCenterBridge.Slot);

        UseEffect(() => RefreshSessions(post), DepKey.Empty);

        UseSignalEffect(() =>
        {
            _ = _session.Value;
            _expandedSeq.Value = -1;
            EnsureSessionLoaded(post);
        });

        // Live-log tail poll (session 0 only): a frame-clock interval that AUTO-PAUSES while parked/minimized (idle
        // quiesce) — replaces the old System.Threading.Timer + post marshal. Bumps _refresh only when the log version moved.
        UseInterval(() =>
        {
            long v = WaveeLog.Instance.Version;
            if (v == lastVersion.Value) return;
            lastVersion.Value = v;
            _refresh.Value = _refresh.Peek() + 1;
        }, 750f, enabled: _session.Value == 0);

        // Hoisted here (unconditional) so the hook order is stable — LogBody has early-out branches before the list.
        var logLayout = UseMemo(() => new MeasuredStackVirtualLayout(estimatedExtent: 40f), DepKey.Empty);

        _ = _refresh.Value;
        _ = _diagVersion.Value;
        _ = _expandedSeq.Value;
        _ = _search.Value;
        _ = _level.Value;
        _ = _category.Value;
        _ = _newestFirst.Value;
        _ = _groupRepeats.Value;
        _ = _visibleLimit.Value;
        bool liveSession = _session.Value == 0;
        WaveeLogEntry[]? entries = liveSession ? WaveeLog.Instance.Snapshot()
            : _sessionLoaded == _session.Value ? _sessionEntries : null;
        var visible = entries is null ? new List<LogRowData>()
            : Filter(entries, _search.Value, _level.Value, _category.Value, _newestFirst.Value != 0, _groupRepeats.Value != 0);

        return new BoxEl
        {
            Grow = 1f, Shrink = 1f, MinHeight = 0f, Direction = 1, Gap = Spacing.M,
            Children =
            [
                DiagnosticSwitches(),
                new BoxEl
                {
                    Grow = 1f, Shrink = 1f, MinHeight = 0f, Direction = 1,
                    Corners = CornerRadius4.All(Radii.Card), Fill = Tok.FillCardSecondary,
                    BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, ClipToBounds = true,
                    Children =
                    [
                        Toolbar(hooks, entries, visible, liveSession, post),
                        Divider(),
                        LogBody(entries, visible, logLayout),
                        Divider(),
                        Footer(entries, visible, liveSession),
                    ],
                },
            ],
        };
    }

    // ── the switches: the app's THREE diagnostic dials, above the log ────────────────────────────────────────────────
    //
    // These sit ABOVE the log viewer rather than inside its toolbar because they are settings, not filters: they change
    // what the APP does, not what this list shows. The log card keeps Grow=1 below them, so the viewer still owns every
    // pixel the three rows do not.
    //
    // Developer mode is the app's ONE switch for developer surface (App/DeveloperMode.cs): the sidebar's API console,
    // Library V3's overflow entry, the lyrics inspector, the per-topic "Send event" rows and the home image tracer all
    // read it. The other two are independently expensive things a developer turns on deliberately, so they are their
    // own rows rather than riders on the first — but the FPS overlay is meaningless without the developer surface it
    // belongs to, so it is disabled (not hidden) while developer mode is off: a greyed row with its own subtitle says
    // "turn the switch above on", where a vanished row would just look like a missing feature.
    Element DiagnosticSwitches()
    {
        // `.Value`, so flipping developer mode re-renders this tab: the FPS row's enabled state follows it, and every
        // OTHER developer surface in the app is following the same signal at the same moment. The toggles below write
        // settings and then bump `_diagVersion`, which Render already reads — that is what re-seeds their signals.
        bool dev = DeveloperMode.Enabled.Value;

        var rows = new List<Element>(4)
        {
            SwitchRow(Strings.Settings.Diag.DeveloperMode, Strings.Settings.Diag.DeveloperModeSub,
                Icons.Code, dev, isEnabled: true, on => DeveloperMode.Set(settings, on)),

            SwitchRow(Strings.Settings.Diag.FpsOverlay, Strings.Settings.Diag.FpsOverlaySub,
                Icons.Clock, DeveloperMode.FpsOverlay.Value, isEnabled: dev,
                on => DeveloperMode.SetFpsOverlay(settings, on)),

            SwitchRow(Strings.Settings.Diag.DealerArchive, Strings.Settings.Diag.DealerArchiveSub,
                Icons.RadioTower, settings?.Get(WaveeSettings.DealerArchiveEnabled) ?? false, isEnabled: true,
                on =>
                {
                    settings?.Set(WaveeSettings.DealerArchiveEnabled, on);
                    // The archive keeps the directory Program.cs gave it, so the toggle only has to say on/off —
                    // and it applies to the LIVE dealer connection, which is the whole point of it being a setting.
                    DealerArchive.Instance.SetEnabled(on);
                }),
        };

        // Developer surface only: walk the whole update state machine (available → downloading → installing →
        // completed → failed) with no network and no package deployment, so every toast, notification-centre row and
        // About-tab state can be SEEN rather than reasoned about. Composed away entirely when developer mode is off.
        if (dev)
            rows.Add(SettingsCard.Create(new SettingsCard.Options
            {
                Header = Loc.Get(Strings.Settings.Diag.SimulateUpdate),
                Description = Loc.Get(Strings.Settings.Diag.SimulateUpdateSub),
                HeaderIcon = Icons.Download,
                Content = Button.Standard(Loc.Get(Strings.Settings.Diag.SimulateUpdateButton),
                    () => { if (_nc is { } nc) FakeAppUpdateService.Start(nc); }),
                IsEnabled = _nc is not null,
            }));

        // The crash-reports listing lives at the end of this switches block, not inside the log card below it — it
        // is "what crashed", not "what was logged", and belongs beside the other diagnostic surfaces a reporter
        // reaches for first.
        rows.Add(Embed.Comp(() => new CrashReportsCard()));

        return new BoxEl
        {
            Direction = 1, Gap = Spacing.XS, Shrink = 0f, AlignSelf = FlexAlign.Stretch,
            Children = rows.ToArray(),
        };
    }

    /// <summary>One diagnostic on/off row. The toggle is fed a FRESH signal seeded from the current value and re-read
    /// on every render (the <c>SettingsPage</c> appearance-toggle pattern): the truth lives in settings, never in a
    /// mirror signal that could drift from it.</summary>
    Element SwitchRow(string labelKey, string subKey, string icon, bool value, bool isEnabled, Action<bool> onSet)
        => SettingsCard.Create(new SettingsCard.Options
        {
            Header = Loc.Get(labelKey),
            Description = Loc.Get(subKey),
            HeaderIcon = icon,
            IsEnabled = isEnabled,
            Content = ToggleSwitch.Create(new Signal<bool>(value), onChange: _ =>
            {
                onSet(!value);
                _diagVersion.Value = _diagVersion.Peek() + 1;
            }, style: SettingsCard.CompactToggleStyle()),
        });

    void RefreshSessions(Action<Action> post, bool force = false)
    {
        if (!force && (_sessions is not null || _sessionsBusy)) return;
        _sessionsBusy = true;
        // BasePath, not FilePath: discovery globs "<root>-*.log" off this path, so the DATED live file would
        // narrow the glob to one day's rolls. The base (wavee.log) yields the whole wavee-*.log set.
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
                _diagVersion.Value = _diagVersion.Peek() + 1;
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
                _diagVersion.Value = _diagVersion.Peek() + 1;
                EnsureSessionLoaded(post);
            });
        });
    }

    (string[] labels, string[] subs) BuildSessionItems()
    {
        var labels = new List<string>(1 + (_sessions?.Count ?? 0));
        var subs = new List<string>(labels.Capacity);
        labels.Add(Loc.Get(Strings.Settings.Diagnostics.CurrentRun));
        subs.Add(LiveSessionSubtitle());
        if (_sessions is { } list)
            foreach (var s in list)
            {
                labels.Add(SessionLabel(s));
                subs.Add(Loc.Get(Strings.Settings.Diagnostics.PastSessionSub));
            }
        return (labels.ToArray(), subs.ToArray());
    }

    string LiveSessionSubtitle()
    {
        DateTimeOffset started;
        try { started = System.Diagnostics.Process.GetCurrentProcess().StartTime; }
        catch { started = DateTimeOffset.Now; }
        var up = DateTimeOffset.Now - started;
        string uptime = up.TotalHours >= 1
            ? $"{(int)up.TotalHours} h {up.Minutes} m"
            : up.TotalMinutes >= 1 ? $"{(int)up.TotalMinutes} min" : "just now";
        return $"pid {Environment.ProcessId} · running {uptime}";
    }

    static string SessionLabel(WaveeLogSessions.Info s) =>
        (s.StartUnixMs > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(s.StartUnixMs).ToLocalTime().ToString("MMM d · HH:mm", CultureInfo.InvariantCulture)
            : "pid " + s.Pid.ToString(CultureInfo.InvariantCulture))
        + " · " + s.EntryCount.ToString(CultureInfo.InvariantCulture) + " events";

    Element Toolbar(InputHooks hooks, WaveeLogEntry[]? entries, List<LogRowData> visible, bool live, Action<Action> post)
    {
        int warn = 0, err = 0;
        if (entries is not null)
            foreach (var e in entries)
            {
                if (e.Level == WaveeLogLevel.Warning) warn++;
                else if (e.Level >= WaveeLogLevel.Error) err++;
            }

        var (labels, subs) = BuildSessionItems();

        return new BoxEl
        {
            Direction = 1, Gap = Spacing.S,
            Padding = new Edges4(Spacing.L, Spacing.S, Spacing.M, Spacing.S),
            Children =
            [
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, MinHeight = 40f,
                    Children =
                    [
                        ComboBox.Create(labels, _session, width: 300f, itemDescriptions: subs,
                            onChange: _ => _expandedSeq.Value = -1),
                        warn > 0 ? ClickableBadge(InfoBadge.Count(warn, InfoBadgeSeverity.Caution), () => _level.Value = 2) : new BoxEl(),
                        err > 0 ? ClickableBadge(InfoBadge.Count(err, InfoBadgeSeverity.Critical), () => _level.Value = 3) : new BoxEl(),
                        new BoxEl { Grow = 1f },
                        Embed.Comp(() => new DiagnosticsMoreMenu(BuildOverflowItems(hooks, live, visible, post))) with { Key = "diag-overflow" },
                    ],
                },
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, Wrap = true, MinHeight = 40f,
                    Children =
                    [
                        AutoSuggestBox.Create(Array.Empty<string>(),
                            placeholder: Loc.Get(Strings.Settings.Diagnostics.FilterPlaceholder),
                            grow: 1f, text: _search,
                            onChange: q => _search.Value = q,
                            onQuerySubmitted: q => _search.Value = q,
                            minHeight: 34f, cornerRadius: Radii.Control),
                        SelectorBar.Create(s_levelLabels, _level),
                        Embed.Comp(() => new DiagToolbarToggle(Icons.Sort, _newestFirst,
                            Loc.Get(Strings.Settings.Diagnostics.SortNewestTip),
                            Loc.Get(Strings.Settings.Diagnostics.SortOldestTip),
                            () => _expandedSeq.Value = -1)),
                        Embed.Comp(() => new DiagToolbarToggle(Icons.List, _groupRepeats,
                            Loc.Get(Strings.Settings.Diagnostics.GroupRepeatsTip),
                            Loc.Get(Strings.Settings.Diagnostics.GroupRepeatsOffTip),
                            () => _expandedSeq.Value = -1)),
                        LevelCombo(isFile: false),
                        LevelCombo(isFile: true),
                    ],
                },
            ],
        };
    }

    // A live log-level dropdown: writes the WaveeLog gate immediately and persists the choice. When the matching env var
    // overrides the level (WaveeLog.Configure applied it at startup) the box is disabled with an "overridden by env" note.
    Element LevelCombo(bool isFile)
    {
        bool envSet = isFile ? WaveeLog.EnvFileMinLevelSet : WaveeLog.EnvMinLevelSet;
        string desc = envSet
            ? Loc.Get(isFile ? Strings.Settings.Diagnostics.LevelOverriddenFile : Strings.Settings.Diagnostics.LevelOverriddenMin)
            : Loc.Get(isFile ? Strings.Settings.Diagnostics.FileLevelSub : Strings.Settings.Diagnostics.CaptureLevelSub);
        return ComboBox.Create(s_logLevels, isFile ? _fileLevel : _minLevel, width: 132f,
            header: Loc.Get(isFile ? Strings.Settings.Diagnostics.FileLevel : Strings.Settings.Diagnostics.CaptureLevel),
            description: desc,
            isEnabled: !envSet,
            onChange: i =>
            {
                var lvl = (WaveeLogLevel)Math.Clamp(i, 0, 4);
                if (isFile)
                {
                    WaveeLog.Instance.FileMinLevel = lvl;
                    settings?.Set(WaveeSettings.LogFileMinLevel, i);
                }
                else
                {
                    WaveeLog.Instance.MinLevel = lvl;
                    settings?.Set(WaveeSettings.LogMinLevel, i);
                }
            });
    }

    static Element ClickableBadge(BoxEl badge, Action onClick) =>
        badge with { Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = onClick };

    List<MenuFlyoutItem> BuildOverflowItems(InputHooks hooks, bool live, List<LogRowData> visible, Action<Action> post)
    {
        var copy = visible.ToArray();
        var cats = new List<MenuFlyoutItem>(s_categories.Length);
        for (int i = 0; i < s_categories.Length; i++)
        {
            int idx = i;
            cats.Add(MenuFlyoutItem.RadioItem(s_categories[i], _category.Peek() == idx, () => _category.Value = idx));
        }

        return
        [
            new(Loc.Get(Strings.Settings.Diagnostics.CopyVisible), Icons.Copy, true,
                () => hooks.Clipboard?.SetText(BuildCopyText(copy))),
            MenuFlyoutItem.Toggle(Loc.Get(Strings.Settings.Diagnostics.NewestFirst), _newestFirst.Peek() != 0,
                () => _newestFirst.Value = _newestFirst.Peek() == 0 ? 1 : 0, Icons.Sort),
            MenuFlyoutItem.Toggle(Loc.Get(Strings.Settings.Diagnostics.GroupRepeats), _groupRepeats.Peek() != 0,
                () => _groupRepeats.Value = _groupRepeats.Peek() == 0 ? 1 : 0, Icons.List),
            MenuFlyoutItem.Separator,
            MenuFlyoutItem.SubMenu(Loc.Get(Strings.Settings.Diagnostics.Category), cats, Icons.Filter),
            MenuFlyoutItem.Separator,
            new(Loc.Get(Strings.Settings.Diagnostics.ExportSession), Icons.Download, true, () => ExportSession(hooks, copy)),
            new(Loc.Get(Strings.Report.ThisSession), Icons.Attention, true,
                () => ReportRequests.Open(ReportKind.Bug, new ReportPrefill(PastSession: SelectedPastSession()))),
            new(Loc.Get(Strings.Settings.Diagnostics.OpenLogFolder), Icons.Folder, true,
                () => SettingsShared.OpenFolder(Path.GetDirectoryName(WaveeLog.Instance.FilePath ?? "") ?? SettingsShared.AppDataRoot)),
            new(Loc.Get(Strings.Settings.Diagnostics.RefreshSessions), Icons.Refresh, true,
                () => RefreshSessions(post, force: true)),
            MenuFlyoutItem.Separator,
            new(Loc.Get(Strings.Settings.Diagnostics.ClearRing), Icons.ClearText, live,
                () => SettingsShared.Confirm(_overlay,
                    Loc.Get(Strings.Settings.Diagnostics.ClearRing),
                    Loc.Get(Strings.Settings.Diagnostics.ClearRingBody),
                    Loc.Get(Strings.Settings.Diagnostics.ClearRing),
                    () => { WaveeLog.Instance.ClearRing(); _refresh.Value = _refresh.Peek() + 1; })),
        ];
    }

    /// <summary>The past session the picker currently has selected, or null when the live ring (index 0) is
    /// selected — <see cref="ReportComposer.Compose"/> reads null as "use the live ring" the same way the rest
    /// of this panel does.</summary>
    WaveeLogSessions.Info? SelectedPastSession()
    {
        int sel = _session.Peek();
        if (sel == 0 || _sessions is not { } sessions || sel - 1 >= sessions.Count) return null;
        return sessions[sel - 1];
    }

    void ExportSession(InputHooks hooks, IReadOnlyList<LogRowData> visible)
    {
        string defaultName = _session.Value == 0
            ? "wavee-session-live.txt"
            : "wavee-session-" + _session.Value + ".txt";
        string? path = FilePicker.SaveFile(FluentApp.WindowHandle,
            Loc.Get(Strings.Settings.Diagnostics.ExportSession),
            defaultName,
            new ("Log text", "*.txt"), new ("All files", "*.*"));
        if (path is null) return;

        try
        {
            if (_session.Value == 0)
                File.WriteAllText(path, BuildCopyText(visible));
            else if (_sessions is { } sessions && _session.Value - 1 < sessions.Count)
                WaveeLogSessions.ExportSessionToFile(sessions[_session.Value - 1], path);
        }
        catch { /* best-effort */ }
    }

    Element LogBody(WaveeLogEntry[]? entries, List<LogRowData> visible, MeasuredStackVirtualLayout layout)
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

        if (visible.Count == 0)
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

        int sort = _newestFirst.Value;
        int group = _groupRepeats.Value;
        string listKey = "settings:logs:" + _session.Value + ":" + sort + ":" + group + ":" + _visibleLimit.Value;
        // ItemsView is an autonomous component: its ItemCount/ItemTemplate freeze at first mount. The filter level/
        // category/search and the live-growing count are NOT carried reactively, so we REMOUNT the list whenever the
        // visible SET changes (the DetailTracks re-key idiom). scrollKey (listKey) restores the offset across remounts.
        string remountKey = listKey + ":L" + _level.Value + ":C" + _category.Value
            + ":n" + visible.Count + ":q" + _search.Value.Length;
        return new BoxEl
        {
            Key = "diaglist:" + remountKey,
            Grow = 1f, Shrink = 1f, MinHeight = 0f,
            Children =
            [
                ItemsView.Create(
                    visible.Count, i => LogRow(visible[i]),
                    RepeatLayout.Measured(layout),
                    new ListOptions
                    {
                        SelectionMode = ItemsSelectionMode.Single,
                        Controller = _listCtrl,
                        Selector = SelectorVisual.AccentPill,
                        KeyOf = i => listKey + ":" + visible[i].Entry.Sequence,
                        IsItemInvokedEnabled = true,
                        OnInvoked = i => ToggleExpand(visible[i].Entry.Sequence, visible),
                        Grow = 1f,
                        Scroll = new ScrollOptions { ScrollKey = listKey },
                    }),
            ],
        };
    }

    void ToggleExpand(long seq, List<LogRowData> visible)
    {
        _expandedSeq.Value = _expandedSeq.Peek() == seq ? -1 : seq;
        for (int i = 0; i < visible.Count; i++)
        {
            if (visible[i].Entry.Sequence != seq) continue;
            _listCtrl.StartBringItemIntoView(i, alignmentRatio: 0f);
            break;
        }
    }

    Element LogRow(LogRowData row)
    {
        var e = row.Entry;
        bool expanded = _expandedSeq.Value == e.Sequence;

        var line = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S,
            MinHeight = 40f, Padding = new Edges4(Spacing.M, 0f, Spacing.M, 0f), Grow = 1f,
            Children =
            [
                SeverityDot(e.Level),
                new TextEl(e.UnixMs > 0 ? FmtTime(e.UnixMs) : "—")
                    { Size = 12f, Color = Tok.TextSecondary, FontFamily = "Cascadia Code", Width = 88f, Shrink = 0f },
                LevelPill(e.Level),
                new TextEl(e.Category) { Size = 12f, Color = Tok.TextSecondary, FontFamily = "Cascadia Code", Shrink = 0f },
                new TextEl("#" + e.Sequence) { Size = 12f, Color = Tok.TextTertiary, FontFamily = "Cascadia Code", Shrink = 0f },
                new TextEl(e.Message)
                {
                    Size = 13f, Color = Tok.TextPrimary, Grow = 1f,
                    Wrap = expanded ? TextWrap.Wrap : TextWrap.NoWrap,
                    Trim = expanded ? TextTrim.None : TextTrim.CharacterEllipsis,
                    MaxLines = expanded ? 0 : 1,
                },
                row.Repeat > 1 ? RepeatBadge(row.Repeat) : new BoxEl(),
                new TextEl(expanded ? Icons.ChevronDown : Icons.ChevronRight)
                    { Size = 12f, Color = Tok.TextTertiary, FontFamily = Theme.IconFont, Shrink = 0f },
            ],
        };

        if (!expanded) return line;

        var detail = new List<Element> { line };
        string fieldText = FieldText(e.Fields);
        if (fieldText.Length > 0) detail.Add(MonoLine(fieldText, Tok.TextSecondary));
        if (e.Exception is { Length: > 0 } ex) detail.Add(MonoLine(ex, Tok.SystemFillCritical));
        return new BoxEl { Direction = 1, Gap = 4f, Padding = new Edges4(0, 0, Spacing.S, Spacing.S), Children = detail.ToArray() };
    }

    static Element MonoLine(string text, ColorF color) =>
        new TextEl(text) { Size = 12f, Color = color, FontFamily = "Cascadia Code", Wrap = TextWrap.Wrap };

    static Element RepeatBadge(int repeat) => new BoxEl
    {
        Padding = new Edges4(7f, 1f, 7f, 2f), Corners = CornerRadius4.All(Radii.Full),
        Fill = Tok.FillSubtleSecondary,
        Children = [new TextEl("×" + repeat.ToString(CultureInfo.InvariantCulture)) { Size = 10.5f, Weight = 700, Color = Tok.TextSecondary }],
    };

    static Element SeverityDot(WaveeLogLevel level) => level switch
    {
        WaveeLogLevel.Warning => InfoBadge.Dot(InfoBadgeSeverity.Caution),
        >= WaveeLogLevel.Error => InfoBadge.Dot(InfoBadgeSeverity.Critical),
        _ => new BoxEl { Width = 6f, Height = 6f, Shrink = 0f },
    };

    Element Footer(WaveeLogEntry[]? entries, List<LogRowData> visible, bool live)
    {
        int total = entries?.Length ?? 0;
        int shown = visible.Count;
        int limit = Math.Min(MaxVisibleRows, _visibleLimit.Value);
        bool canLoadMore = shown >= limit && shown < total && entries is not null;

        var kids = new List<Element>
        {
            new TextEl(live
                ? Strings.Settings.Diagnostics.FooterLive(shown, total)
                : Strings.Settings.Diagnostics.FooterPast(shown, total))
            { Size = 12f, Color = Tok.TextSecondary, Grow = 1f },
        };
        if (canLoadMore)
            kids.Add(HyperlinkButton.Create(Loc.Get(Strings.Settings.Diagnostics.LoadMore),
                () => _visibleLimit.Value = _visibleLimit.Peek() + 500));

        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M,
            Padding = new Edges4(Spacing.L, Spacing.S, Spacing.M, Spacing.S),
            Children = kids.ToArray(),
        };
    }

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

    List<LogRowData> Filter(WaveeLogEntry[] entries, string search, int level, int category, bool newestFirst, bool group)
    {
        int cap = Math.Min(MaxVisibleRows, _visibleLimit.Value);
        var result = new List<LogRowData>(Math.Min(entries.Length, cap));
        int start = newestFirst ? entries.Length - 1 : 0;
        int end = newestFirst ? -1 : entries.Length;
        int step = newestFirst ? -1 : 1;
        string cat = (uint)category < (uint)s_categories.Length ? s_categories[category] : "All";

        for (int i = start; i != end && result.Count < cap; i += step)
        {
            var e = entries[i];
            if (!PassesLevel(e.Level, level)) continue;
            if (cat != "All" && !string.Equals(e.Category, cat, StringComparison.OrdinalIgnoreCase)) continue;
            if (search.Length > 0 && !PassesSearch(e, search)) continue;

            if (group && result.Count > 0)
            {
                var last = result[^1].Entry;
                if (last.Level == e.Level && last.Category == e.Category && last.EventId == e.EventId && last.Message == e.Message)
                {
                    result[^1] = result[^1] with { Repeat = result[^1].Repeat + 1 };
                    continue;
                }
            }
            result.Add(new LogRowData(e, 1));
        }
        return result;
    }

    static bool PassesLevel(WaveeLogLevel level, int filter) => filter switch
    {
        1 => level >= WaveeLogLevel.Info,
        2 => level >= WaveeLogLevel.Warning,
        3 => level >= WaveeLogLevel.Error,
        _ => true,
    };

    static bool PassesSearch(WaveeLogEntry e, string q)
    {
        if (e.Category.Contains(q, StringComparison.OrdinalIgnoreCase)) return true;
        if (e.EventId.Contains(q, StringComparison.OrdinalIgnoreCase)) return true;
        if (e.Message.Contains(q, StringComparison.OrdinalIgnoreCase)) return true;
        if (e.OperationId?.Contains(q, StringComparison.OrdinalIgnoreCase) == true) return true;
        if (e.Fields is { } fields)
            for (int i = 0; i < fields.Length; i++)
                if (fields[i].Name.Contains(q, StringComparison.OrdinalIgnoreCase) || fields[i].Value.Contains(q, StringComparison.OrdinalIgnoreCase))
                    return true;
        return false;
    }

    static string FieldText(WaveeLogField[]? fields)
    {
        if (fields is not { Length: > 0 }) return "";
        var sb = new StringBuilder(fields.Length * 16);
        for (int i = 0; i < fields.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            fields[i].AppendTo(sb);
        }
        return sb.ToString();
    }

    static string BuildCopyText(IReadOnlyList<LogRowData> rows)
    {
        var sb = new StringBuilder(rows.Count * 96);
        for (int i = 0; i < rows.Count; i++)
        {
            var e = rows[i].Entry;
            sb.Append("seq=").Append(e.Sequence.ToString(CultureInfo.InvariantCulture))
              .Append(' ').Append(e.Format());
            if (rows[i].Repeat > 1)
                sb.Append(" (repeated ").Append(rows[i].Repeat.ToString(CultureInfo.InvariantCulture)).Append("×)");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    static string FmtTime(long unixMs) =>
        DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

    sealed class DiagnosticsMoreMenu(List<MenuFlyoutItem> items) : Component
    {
        public override Element Render()
        {
            var anchor = UseRef<NodeHandle>(default);
            var handle = UseRef<OverlayHandle?>(null);
            var svc = UseContext(Overlay.Service);

            void Toggle()
            {
                if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
                handle.Value = svc.Open(
                    () => anchor.Value,
                    () => MenuFlyout.Create(items, () => handle.Value?.Close()),
                    FlyoutPlacement.BottomEdgeAlignedRight,
                    new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false });
                handle.Value.ClosedAction = () => handle.Value = null;
            }

            return new BoxEl
            {
                Width = 32f, Height = 32f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Role = AutomationRole.Button, Focusable = true, OnClick = Toggle, Cursor = CursorId.Hand,
                OnRealized = h => anchor.Value = h,
                Children = [new TextEl(Icons.More) { Size = 16f, FontFamily = Theme.IconFont, Color = Tok.TextSecondary }],
            }.Interactive(Interaction.Subtle);
        }
    }

    /// <summary>Reactive toolbar toggle — own Component so signal writes re-render chrome + tooltip.</summary>
    sealed class DiagToolbarToggle(string glyph, Signal<int> sig, string tipOn, string tipOff, Action? onToggle) : Component
    {
        public override Element Render()
        {
            bool on = sig.Value != 0;
            var style = IconButton.DefaultStyle with
            {
                Foreground = on ? Tok.AccentDefault : Tok.TextSecondary,
                HoverForeground = on ? Tok.AccentDefault : Tok.TextPrimary,
                Fill = on ? Tok.AccentDefault with { A = 0.14f } : ColorF.Transparent,
                HoverFill = on ? Tok.AccentDefault with { A = 0.20f } : Tok.FillSubtleSecondary,
            };
            var btn = IconButton.Create(glyph, () =>
            {
                sig.Value = sig.Peek() == 0 ? 1 : 0;
                onToggle?.Invoke();
            }, style) with { Role = AutomationRole.ToggleButton };
            return ToolTip.Wrap(btn, on ? tipOn : tipOff);
        }
    }
}
