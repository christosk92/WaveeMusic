// ── Screens/Diagnostics.UI.cs ──────────────────────────────────────────────────────────────────────────────────────
// the diagnostics install (the orchestrator's one call), the two diagnostics pages (runtime, Connect — the API console
// page DELETED, §9.6 Q7, 2026-09-12), the logs panel + log view, the FPS overlay, the shared diagnostics chrome, and the
// lyrics inspector dialog (≈600, moved here by ch 22 §9 (d), A14)
//
// Role: UI
// Owner: S
// Wave: 6
// Budget: 2300 lines
// Spec: ch 27 §9.3 (1,700) + ch 22 §9 (d) (+600) — the 1,700 figure predates Q7 and is not reduced here; ch 27 owns the
//       precise re-split. Wireframes: ch 27 W21-W24, W26, W28; ch 22 W17-W19b. Gaps G-094, G-153, G-197, G-218.
//
// THE RULE THIS FILE KEEPS: it lays out and binds, it never decides. The filter/group/cap of the log view, the level fold,
// the report text, the frame watch and the inspector's anomaly inks are `Diagnostics.cs` / `Platform.Settings.cs` (CORE,
// tested); the disk and the process are `Diagnostics.Host.cs`.
//
// NOTHING HERE READS A CLOCK PER FRAME (ch 27 N12): the log tail polls at 750 ms and bumps only when `Log.Version` moved
// (and not at all on a past session), and the FPS pill renders ONCE — the host refreshes its two retained dynamic-text
// slots in place. No surface here carries a context menu, a drag or a shortcut (ch 27 §6).
//
// PROPS FREEZE AT MOUNT: every control whose interesting fields freeze (the session / category / level combos, the command
// bar, the virtualized list) is REMOUNTED through a `Key` that changes exactly when its frozen input does, and every
// toggle that triggers such a remount runs through `UsePost` so the click finishes before its node is torn down.

using System.Globalization;
using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using FluentGpu.WindowsApi.Dialogs;

namespace Wavee;

public static partial class Diagnostics
{
    // ══ 1. THE INSTALL (the orchestrator's one call) ════════════════════════════════════════════════════════════════

    static int s_installed;

    /// <summary>Install everything owner S owns for a GUI launch. Called ONCE by the composition root AFTER
    /// <c>Shell.InstallMarshallers()</c> (the network host posts through <c>Playback.ToUi</c>), <c>Shell.InstallUi()</c> and
    /// <c>Settings.InstallScreens()</c>, and BEFORE <c>Update.Host.Start</c> (the crash prompt reads <c>app.lastRunVersion</c>
    /// before the updater rewrites it) and <c>Shell.Run()</c> (the pre-loop hook must be set before the window). Never from
    /// the headless arm.
    /// <list type="bullet">
    /// <item>the run marker, the WER dump probe and the crash-prompt latch;</item>
    /// <item>the two diagnostics pages (`playback-diagnostics`, `connect-diagnostics`);</item>
    /// <item>the frame watch as <c>Shell.RouteNoted</c>'s consumer (G-197);</item>
    /// <item>the NLM cost host; the pre-loop hook (ambient power + the GUI probe arms);</item>
    /// <item>the seams the Wave-6 screens left for owner S: the logs panel, "Send event", the report dialog's diagnostics
    /// text / past sessions / crash files / crash probe, and the updater's metered read.</item>
    /// </list></summary>
    public static void Install()
    {
        if (Interlocked.Exchange(ref s_installed, 1) != 0) return;
        BeginGuiRun();

        Shell.SetPage(Shell.RouteKind.PlaybackDiagnostics, static (in Shell.Route _) => RuntimePage());
        Shell.SetPage(Shell.RouteKind.ConnectDiagnostics, static (in Shell.Route _) => ConnectPage());
        Shell.RouteNoted += static route => NavigationFrameWatch.NoteRoute(route);
        NavigationFrameWatch.Attach();

        Platform.Network.Install(Playback.ToUi);
        Probe.InstallGuiArms(Environment.GetCommandLineArgs());

        Settings.LogsPanelBody = static () => LogsPanel();
        Settings.SendTestEvent = static topic => ToTestEvent(NotificationSimulator.Send(topic));
        Feedback.DiagnosticsText = static () => InfoText();
        Feedback.PastSessionLog = static key => PastSessionLines(key);
        Feedback.ListCrashReports = static max => CrashReport.List(max);
        Feedback.CrashProbeMode = static () => Probe.CrashProbeMode;
        Update.Host.IsMetered = static () => Platform.Network.IsMetered;
        // The stream sizes its read-ahead down on a metered link (ch 29 W9's invisible effect): a volatile snapshot read,
        // any thread. The headless arm leaves the seam null = unmetered, which is what an unknown cost is.
        Spotify.Audio.MeteredConnection = static () => Platform.Network.IsMetered;
    }

    /// <summary>The exit tail (before <c>Platform.Shutdown()</c>): the last frame/memory lines, the clean run marker, the
    /// cost subscriptions and the power poll.</summary>
    public static void Shutdown()
    {
        NavigationFrameWatch.EndSession();
        MemorySampler.SampleProcessEnd();
        try { RunMarker.End(Platform.Settings); } catch { }
        Platform.Network.Shutdown();
        Platform.DetachAmbientPower();
    }

    static Settings.TestEventResult ToTestEvent(SimResult r) => new(r.Outcome switch
    {
        SimOutcome.Dropped => Settings.TestEventOutcome.Dropped,
        SimOutcome.RecordedInApp => Settings.TestEventOutcome.RecordedInApp,
        SimOutcome.Banner => Settings.TestEventOutcome.Banner,
        SimOutcome.BannerQuietDeferred => Settings.TestEventOutcome.BannerQuietDeferred,
        SimOutcome.Scheduled => Settings.TestEventOutcome.Scheduled,
        SimOutcome.NeverBanners => Settings.TestEventOutcome.NeverBanners,
        _ => Settings.TestEventOutcome.Unavailable,
    }, r.At);

    /// <summary>"Copy diagnostics info" / the report dialog's diagnostics block (0.2.9 <c>SettingsPage.DiagInfoText</c>):
    /// the build stamp, OS, engine, GPU, data folder, feed and the runtime's state. UI thread.</summary>
    public static string InfoText()
    {
        string arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        var tier = GpuProfile.Tier switch { GpuPowerTier.Weak => Settings.GpuTier.Weak, GpuPowerTier.Strong => Settings.GpuTier.Strong, _ => Settings.GpuTier.Unknown };
        var runtime = Setup.Runtime.Status.Peek();
        return Platform.Version.OneLine(arch)
            + "\nOS: " + System.Runtime.InteropServices.RuntimeInformation.OSDescription
            + "\nEngine: FluentGpu · .NET " + Environment.Version
            + "\nGPU: " + Settings.Receipts.GpuLine(GpuProfile.AdapterName, tier, GpuProfile.IsSoftwareAdapter)
            + "\nData folder: " + Platform.LocalFolder
            + "\nFeed: " + OrDash(Update.Host.FeedUrl)
            + "\nPlayback runtime: " + (runtime.IsReady ? "Ready" : runtime.Issue.ToString()) + "\n";
    }

    /// <summary>The report dialog's past-session quote: the session <see cref="WaveeLogSessions.KeyOf"/> named, or the newest
    /// past session for a null key. Off the UI thread.</summary>
    static IReadOnlyList<string>? PastSessionLines(string? key)
    {
        var sessions = WaveeLogSessions.ListPastSessions(Log.BasePath, Environment.ProcessId);
        return WaveeLogSessions.Find(sessions, key) is { } info ? WaveeLogSessions.RawLines(info, WaveeLogSessions.ReadSharedLines) : null;
    }

    // ══ 2. THE MOUNT POINTS ═════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Settings › Logs: the full-height viewer (ch 27 W21-W22). It fills the lane the Settings page gives it.</summary>
    // MOUNT POINT (stage B contract) — assigned to `Settings.LogsPanelBody` by Install
    public static Element LogsPanel() => Embed.Comp(static () => new LogsPanelView());

    /// <summary>The `playback-diagnostics` page (ch 27 W23).</summary>
    // MOUNT POINT (stage B contract) — registered by Install
    public static Element RuntimePage() => Embed.Comp(static () => new RuntimePageView());

    /// <summary>The `connect-diagnostics` page (ch 27 W24).</summary>
    // MOUNT POINT (stage B contract) — registered by Install
    public static Element ConnectPage() => Embed.Comp(static () => new ConnectPageView());

    /// <summary>The FPS HUD (ch 27 W26) for the shell frame's overlay ZStack: a full-bleed, hit-test PASS-THROUGH plain
    /// positioner (a component's output would mirror its Grow onto the wrapper and swallow every hit — the 0.2.9 bug that
    /// killed scrolling) holding a content-sized pill, shown only while developer mode AND the overlay toggle are on.</summary>
    // MOUNT POINT (stage B contract) — mounted by the shell frame (owner I / the orchestrator)
    public static Element FpsOverlay() => new BoxEl
    {
        Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, HitTestPassThrough = true,
        Direction = 1, AlignItems = FlexAlign.End, Padding = new Edges4(0f, 104f, 14f, 0f),
        Children =
        [
            Flow.Show(static () => Platform.Developer.ShowsFpsOverlay(Platform.Developer.Enabled.Value, Platform.Developer.FpsOverlay.Value),
                Embed.Comp(static () => new FpsPill())),
        ],
    };

    /// <summary>Renders EXACTLY ONCE: both numbers are retained dynamic-text slots the host refreshes in place, so the HUD
    /// never re-renders per frame and never depresses the rate it displays. First painted frame: two literal "--".</summary>
    sealed class FpsPill : Component
    {
        public override Element Render() => new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = 4f, Padding = new Edges4(8f, 4f, 8f, 4f), Corners = CornerRadius4.All(6f),
            Fill = Tok.FillSolidBase with { A = 0.90f }, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Children =
            [
                new TextEl("--") { Size = 12f, Weight = 700, Color = Tok.AccentTextPrimary, DynamicText = DynamicTextKind.FrameFps },
                new TextEl("fps") { Size = 12f, Weight = 600, Color = Tok.TextSecondary },
                new TextEl("·") { Size = 12f, Weight = 600, Color = Tok.TextTertiary },
                new TextEl("--") { Size = 12f, Weight = 600, Color = Tok.TextSecondary, DynamicText = DynamicTextKind.FrameMs },
                new TextEl("ms") { Size = 12f, Weight = 600, Color = Tok.TextTertiary },
            ],
        };
    }

    // ══ 3. THE SHARED DIAGNOSTICS CHROME (ONE copy — 0.2.9 had it twice) ════════════════════════════════════════════

    const float PageMaxWidth = 1000f, RuntimeLabelWidth = 132f, ConnectLabelWidth = 160f;

    static Element PageHeader(string glyph, string title) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, Padding = new Edges4(Spacing.L, Spacing.L, Spacing.L, Spacing.M),
        Children = [FluentGpu.Dsl.Ui.Icon(glyph, 22f, Tok.TextPrimary), Design.Type.PageHero(title) with { Grow = 1f }],
    };

    /// <summary>The pinned header over ONE scroll keyed on the route (ch 27 W28: nothing compacts, nothing is sticky).</summary>
    static Element PageFrame(string glyph, string title, string scrollKey, List<Element> body) => new BoxEl
    {
        Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1,
        Children =
        [
            PageHeader(glyph, title),
            FluentGpu.Dsl.Ui.ScrollView(new BoxEl
            {
                Direction = 1, Gap = 12f, MaxWidth = PageMaxWidth, AlignSelf = FlexAlign.Stretch,
                Padding = new Edges4(Spacing.L, Spacing.L, Spacing.L, Spacing.XXL), Children = body.ToArray(),
            }) with { Grow = 1f, Shrink = 1f, MinHeight = 0f, ScrollKey = scrollKey },
        ],
    };

    static Element Card(string title, List<Element> rows)
    {
        rows.Insert(0, new TextEl(title) { Size = 12f, Weight = 600, Color = Tok.TextSecondary });
        return new BoxEl
        {
            Direction = 1, Gap = 6f, Padding = Edges4.All(12f), Fill = Tok.FillLayerAlt, Corners = CornerRadius4.All(Radii.Control),
            BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, Children = rows.ToArray(),
        };
    }

    static Element Card(string title, params Element[] rows) => Card(title, new List<Element>(rows));

    /// <summary>Label + value; a blank value prints "—" (ch 27 W23: an unknown fact is a dash, never an empty column).</summary>
    static Element Row(string label, string? value, float labelWidth = RuntimeLabelWidth) => new BoxEl
    {
        Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Start,
        Children =
        [
            new TextEl(label) { Size = 12f, Color = Tok.TextSecondary, Width = labelWidth, Shrink = 0f },
            new TextEl(OrDash(value)) { Size = 12f, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap, Grow = 1f, MinWidth = 0f },
        ],
    };

    /// <summary>The presence chip: text is TextPrimary in both states; only the background carries the severity.</summary>
    static Element Chip(string label, bool present) => new BoxEl
    {
        Padding = new Edges4(6f, 1f, 6f, 1f), Corners = CornerRadius4.All(Radii.Control),
        Fill = present ? Tok.SystemFillSuccessBackground : Tok.SystemFillCriticalBackground,
        Children = [new TextEl((present ? "✓ " : "✕ ") + label) { Size = 11f, Color = Tok.TextPrimary }],
    };

    static TextEl Body(string text) => new(text) { Size = 12f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap };

    static TextEl Caption(string text) => new(text) { Size = 11f, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap };

    static BoxEl Separator(float top) => new() { Height = 1f, Fill = Tok.StrokeCardDefault, Margin = new Edges4(0f, top, 0f, 6f) };

    /// <summary>A coloured status glyph + heading, then wrapped body copy (the setup card's inline block: one feature).</summary>
    static Element Status(string glyph, ColorF glyphColor, string heading, string body) => new BoxEl
    {
        Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Start, Padding = Edges4.All(12f), Fill = Tok.FillLayerAlt,
        Corners = CornerRadius4.All(Radii.Control), BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
        Children =
        [
            new TextEl(glyph) { Size = 18f, FontFamily = Theme.IconFont, Color = glyphColor, Shrink = 0f, Margin = new Edges4(0f, 1f, 0f, 0f) },
            new BoxEl
            {
                Direction = 1, Gap = 4f, Grow = 1f, MinWidth = 0f,
                Children =
                [
                    new TextEl(heading) { Size = 14f, Weight = 600, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap },
                    new TextEl(body) { Size = 13f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap },
                ],
            },
        ],
    };

    static TimeSpan LocalOffset => TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.UtcNow);

    // ══ 4. THE LOGS PANEL (ch 27 W21-W22; 0.2.9 `Features/Shell/LogsPanel.cs`) ══════════════════════════════════════

    /// <summary>The viewer. <see cref="_rows"/> and <see cref="_categories"/> are FIELDS, refreshed at the top of every
    /// render, so a command built by an OLDER render (the bar does not remount for a plain search edit) still reads the
    /// CURRENT rows when clicked.</summary>
    sealed class LogsPanelView : Component
    {
        readonly Signal<string> _search = new("");
        readonly Signal<int> _level = new((int)LogLevelBucket.All);
        readonly Signal<int> _category = new(0);
        readonly Signal<int> _session = new(0);
        readonly Signal<int> _newestFirst = new(1);
        readonly Signal<int> _groupRepeats = new(1);
        readonly Signal<int> _wrap = new(0);
        readonly Signal<int> _refresh = new(0);            // the live poll, a load landing, Clear view
        readonly Signal<int> _visibleLimit = new(LogView.PageRows);
        readonly Signal<int> _sessionsRev = new(0);         // ONLY when the session LIST changes
        readonly Signal<int> _levelsRev = new(0);           // Verbose / Capture level / File log level
        readonly Signal<long> _expandedSeq = new(-1);

        List<WaveeLogSessions.Info>? _sessions;
        bool _sessionsBusy, _sessionLoadBusy;
        WaveeLogEntry[]? _sessionEntries;
        int _sessionLoaded;
        LogViewRow[] _rows = [];
        string[] _categories = [];
        readonly ItemsViewController _listCtrl = new();
        IOverlayService? _overlay;

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
            _ = _newestFirst.Value; _ = _groupRepeats.Value; _ = _wrap.Value; _ = _visibleLimit.Value; _ = _levelsRev.Value;

            bool live = _session.Value == 0;
            WaveeLogEntry[]? entries = live ? Log.Snapshot() : _sessionLoaded == _session.Value ? _sessionEntries : null;
            _categories = entries is null ? [] : LogView.Categories(entries);
            // The category clamp lives in an effect (a signal write in render is the backwards-write tripwire).
            UseEffect(() => { if (_category.Peek() > _categories.Length) _category.Value = 0; }, _categories.Length);

            var query = Query();
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
                        Grow = 1f, Shrink = 1f, MinHeight = 0f, Direction = 1, Corners = CornerRadius4.All(Radii.Card),
                        Fill = Tok.FillCardSecondary, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, ClipToBounds = true,
                        Children = [LogBody(entries, result, query, logLayout), new BoxEl { Height = 1f, Fill = Tok.StrokeDividerDefault }, Footer(result, live)],
                    },
                ],
            };
        }

        LogViewQuery Query() => new((LogLevelBucket)_level.Value, CurrentCategory(), _search.Value,
            _newestFirst.Value != 0, _groupRepeats.Value != 0, _visibleLimit.Value);

        // ── the header: the session picker + the command bar ───────────────────────────────────────────────────────

        Element HeaderRow(InputHooks hooks, Action<Action> post, bool live)
        {
            var (labels, subs) = SessionItems();
            return new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, MinHeight = 48f, Shrink = 0f,
                Children =
                [
                    ComboBox.Create(labels, _session, width: 320f, itemDescriptions: subs, onChange: _ => _expandedSeq.Value = -1)
                        with { Key = "logs:session:" + _sessionsRev.Value.ToString(CultureInfo.InvariantCulture) },
                    new BoxEl { Grow = 1f },
                    CommandBar.Create(PrimaryCommands(hooks, post, live), SecondaryCommands(post))
                        with { Key = "logs:bar:" + (live ? "L" : "P") + _newestFirst.Value + _groupRepeats.Value + _wrap.Value + ":" + _levelsRev.Value },
                ],
            };
        }

        IReadOnlyList<AppBarCommand> PrimaryCommands(InputHooks hooks, Action<Action> post, bool live)
        {
            bool newestFirst = _newestFirst.Value != 0, groupRepeats = _groupRepeats.Value != 0;
            bool verbose = LogCapturePolicy.IsVerbose(Log.MinLevel);
            return
            [
                new(Icons.Refresh, Loc.Get(Strings.Settings.Diagnostics.Refresh), () =>
                {
                    RefreshSessions(post, force: true);
                    if (_session.Peek() == 0) _refresh.Value = _refresh.Peek() + 1;
                }),
                new(Icons.Copy, Loc.Get(Strings.Settings.Diagnostics.CopyVisible), () => hooks.Clipboard?.SetText(LogView.CopyText(_rows))),
                new(Icons.Download, Loc.Get(Strings.Settings.Diagnostics.ExportSession), ExportSession),
                new(Icons.Folder, Loc.Get(Strings.Settings.Diagnostics.OpenLogFolder), static () => OpenFolder(Path.GetDirectoryName(Log.FilePath ?? ""))),
                new(Icons.ClearText, Loc.Get(Strings.Settings.Diagnostics.ClearView), () => Controls.Confirm(_overlay,
                    Loc.Get(Strings.Settings.Diagnostics.ClearView), Loc.Get(Strings.Settings.Diagnostics.ClearViewBody),
                    Loc.Get(Strings.Settings.Diagnostics.ClearView), () => { Log.ClearRing(); _refresh.Value = _refresh.Peek() + 1; }),
                    Enabled: LogView.CanClear(_session.Value)),
                AppBarCommand.Separator,
                new(Icons.Sort, Loc.Get(Strings.Settings.Diagnostics.NewestFirst),
                    () => post(() => { _newestFirst.Value = newestFirst ? 0 : 1; _expandedSeq.Value = -1; }),
                    Kind: AppBarCommandKind.ToggleButton, IsChecked: newestFirst),
                new(Icons.List, Loc.Get(Strings.Settings.Diagnostics.GroupRepeats),
                    () => post(() => { _groupRepeats.Value = groupRepeats ? 0 : 1; _expandedSeq.Value = -1; }),
                    Kind: AppBarCommandKind.ToggleButton, IsChecked: groupRepeats),
                new(Icons.Code, Loc.Get(Strings.Settings.Diagnostics.Verbose),
                    () => post(() => { LogCapturePolicy.SetVerbose(Platform.Settings, !verbose); _levelsRev.Value = _levelsRev.Peek() + 1; }),
                    Kind: AppBarCommandKind.ToggleButton, IsChecked: verbose),
            ];
        }

        IReadOnlyList<AppBarCommand> SecondaryCommands(Action<Action> post)
        {
            bool wrap = _wrap.Value != 0;
            return
            [
                new(default, Loc.Get(Strings.Settings.Diagnostics.WrapLines), () => post(() => _wrap.Value = wrap ? 0 : 1),
                    Kind: AppBarCommandKind.ToggleButton, IsChecked: wrap),
                AppBarCommand.Separator,
                new(Icons.Attention, Loc.Get(Strings.Report.ThisSession), () =>
                    Feedback.Open(Feedback.ReportKind.Bug, new Feedback.ReportPrefill(PastSessionId: SelectedPastSession() is { } s ? WaveeLogSessions.KeyOf(s) : null))),
            ];
        }

        // ── the filter row: search + level segments + badges + category + the two capture levels ──────────────────
        // The capture levels are headed ComboBoxes, not cascading overflow radios: a plain CommandBar's overflow renders
        // no sub-menu (ch 27's drift note — the code wins).

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
                    AutoSuggestBox.Create(Array.Empty<string>(), placeholder: Loc.Get(Strings.Settings.Diagnostics.FilterPlaceholder),
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
                        with { Key = "logs:cat:" + _session.Value.ToString(CultureInfo.InvariantCulture) + ":" + _categories.Length.ToString(CultureInfo.InvariantCulture) },
                    ComboBox.Create(LogView.LevelNames, new Signal<int>(Math.Clamp((int)Log.MinLevel, 0, 4)), width: 132f,
                        header: Loc.Get(Strings.Settings.Diagnostics.CaptureLevel),
                        onChange: i => post(() => { LogCapturePolicy.SetMinLevel(Platform.Settings, (WaveeLogLevel)Math.Clamp(i, 0, 4)); _levelsRev.Value = _levelsRev.Peek() + 1; }))
                        with { Key = "logs:level:" + _levelsRev.Value.ToString(CultureInfo.InvariantCulture) },
                    ComboBox.Create(LogView.LevelNames, new Signal<int>(Math.Clamp((int)Log.FileMinLevel, 0, 4)), width: 132f,
                        header: Loc.Get(Strings.Settings.Diagnostics.FileLevel),
                        onChange: i => post(() => { LogCapturePolicy.SetFileLevel(Platform.Settings, (WaveeLogLevel)Math.Clamp(i, 0, 4)); _levelsRev.Value = _levelsRev.Peek() + 1; }))
                        with { Key = "logs:filelevel:" + _levelsRev.Value.ToString(CultureInfo.InvariantCulture) },
                ],
            };
        }

        static SegmentedItem[] s_levelItems = [];

        static SegmentedItem[] LevelItems()
        {
            if (s_levelItems.Length == 0)   // the launch locale is fixed for the process
                s_levelItems =
                [
                    new(Loc.Get(Strings.Settings.Diagnostics.LevelAll)), new(Loc.Get(Strings.Settings.Diagnostics.LevelInfo)),
                    new(Loc.Get(Strings.Settings.Diagnostics.LevelWarnings)), new(Loc.Get(Strings.Settings.Diagnostics.LevelErrors)),
                ];
            return s_levelItems;
        }

        /// <summary>The badge becomes a button that sets the level filter (and gets a name: 0.2.9's had none, ch 27 §6.7).</summary>
        static Element ClickableBadge(BoxEl badge, Action onClick) =>
            badge with { Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = onClick };

        string? CurrentCategory()
        {
            int idx = _category.Value;
            return idx <= 0 || idx > _categories.Length ? null : _categories[idx - 1];
        }

        // ── the sessions: discovery, loading, the picker's items ────────────────────────────────────────────────────

        void RefreshSessions(Action<Action> post, bool force = false)
        {
            if (!force && (_sessions is not null || _sessionsBusy)) return;
            _sessionsBusy = true;
            string? basePath = Log.BasePath;   // the BASE (wavee.log): the dated live file would glob one day's rolls
            int pid = Environment.ProcessId;
            _ = Task.Run(() =>
            {
                var list = WaveeLogSessions.ListPastSessions(basePath, pid);
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
            if (sel == 0 || _sessionLoadBusy || _sessions is not { } sessions || _sessionLoaded == sel || sel - 1 >= sessions.Count) return;
            var info = sessions[sel - 1];
            _sessionLoadBusy = true;
            _ = Task.Run(() =>
            {
                var loaded = WaveeLogSessions.LoadSession(info);
                post(() =>
                {
                    _sessionEntries = loaded;
                    _sessionLoaded = sel;
                    _sessionLoadBusy = false;
                    _refresh.Value = _refresh.Peek() + 1;
                    EnsureSessionLoaded(post);
                });
            });
        }

        (string[] Labels, string[] Subs) SessionItems()
        {
            int n = 1 + (_sessions?.Count ?? 0);
            var labels = new string[n];
            var subs = new string[n];
            labels[0] = Loc.Get(Strings.Settings.Diagnostics.CurrentRun);
            DateTimeOffset started;
            try { using var p = System.Diagnostics.Process.GetCurrentProcess(); started = p.StartTime; }
            catch (InvalidOperationException) { started = DateTimeOffset.Now; }
            subs[0] = Strings.Settings.Diagnostics.RunningFor(Environment.ProcessId, LogView.Uptime(DateTimeOffset.Now - started));
            if (_sessions is { } list)
                for (int i = 0; i < list.Count; i++)
                {
                    labels[i + 1] = LogView.SessionLabel(list[i].StartUnixMs, list[i].Pid, list[i].EntryCount, LocalOffset);
                    subs[i + 1] = Strings.Settings.Diagnostics.SessionEvents(list[i].EntryCount);
                }
            return (labels, subs);
        }

        WaveeLogSessions.Info? SelectedPastSession()
        {
            int sel = _session.Peek();
            return sel == 0 || _sessions is not { } sessions || sel - 1 >= sessions.Count ? null : sessions[sel - 1];
        }

        void ExportSession()
        {
            bool live = _session.Peek() == 0;
            string? path;
            try
            {
                path = FilePicker.SaveFile(FluentApp.WindowHandle, Loc.Get(Strings.Settings.Diagnostics.ExportSession),
                    live ? "wavee-session-live.txt" : "wavee-session-" + _session.Peek().ToString(CultureInfo.InvariantCulture) + ".txt",
                    ("Log text", "*.txt"), ("All files", "*.*"));
            }
            catch (InvalidOperationException ex) { Log.Warn("log", "the export dialog failed", ex); return; }
            if (path is null) return;
            try
            {
                if (live) File.WriteAllText(path, LogView.CopyText(_rows));
                else if (SelectedPastSession() is { } info) WaveeLogSessions.ExportSessionToFile(info, path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn("log", "the session export failed", ex); }
        }

        // ── the body: loading / empty / rows ─────────────────────────────────────────────────────────────────────────

        Element LogBody(WaveeLogEntry[]? entries, LogViewResult result, LogViewQuery query, MeasuredStackVirtualLayout layout)
        {
            if (entries is null)
                return new BoxEl
                {
                    Grow = 1f, Direction = 1, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Gap = Spacing.M,
                    Children = [ProgressRing.Indeterminate(), new TextEl(Loc.Get(Strings.Settings.Diagnostics.LoadingSession)) { Size = 12f, Color = Tok.TextSecondary }],
                };
            if (result.Shown == 0)
                return new BoxEl
                {
                    Grow = 1f, Direction = 1, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Gap = Spacing.M,
                    Padding = new Edges4(0f, 64f, 0f, 64f),
                    Children = [FluentGpu.Dsl.Ui.Icon(Icons.Search, 36f, Tok.TextTertiary), Design.Type.PageHero(Loc.Get(Strings.Settings.Diagnostics.EmptyFilter))],
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

        /// <summary>36 DIP, fixed columns: chevron 12 · dot 6 · time 92 · pill 58 × 22 · category 96 · message · ×N.</summary>
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
                    Sidebar.Chevron.Disclosure(() => _expandedSeq.Value == seq, size: 12f),
                    SeverityDot(e.Level),
                    new TextEl(LogView.FormatTime(e.UnixMs, LocalOffset)) { Size = 12f, Color = Tok.TextSecondary, FontFamily = "Cascadia Code", Width = 92f, Shrink = 0f },
                    LevelPill(e.Level),
                    new TextEl(e.Category) { Size = 12f, Color = Tok.TextSecondary, FontFamily = "Cascadia Code", Width = 96f, Shrink = 0f, Trim = TextTrim.CharacterEllipsis },
                    new TextEl(e.Message)
                    {
                        Size = 13f, Color = Tok.TextPrimary, Grow = 1f, MinWidth = 0f,
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
            if (fieldText.Length > 0) detail.Add(DetailSection(Loc.Get(Strings.Settings.Diagnostics.Fields), fieldText));
            if (e.Exception is { Length: > 0 } ex) detail.Add(DetailSection(Loc.Get(Strings.Settings.Diagnostics.Exception), ex));
            detail.Add(new TextEl(LogView.MetaLine(in e)) { Size = 11f, Color = Tok.TextTertiary, FontFamily = "Cascadia Code", Margin = new Edges4(44f, 0f, 0f, 0f) });
            return new BoxEl { Key = "logs:row:" + seq.ToString(CultureInfo.InvariantCulture), Direction = 1, Gap = 4f, Padding = new Edges4(0f, 0f, Spacing.S, Spacing.S), Children = detail.ToArray() };
        }

        static Element DetailSection(string caption, string text) => new BoxEl
        {
            Direction = 1, Gap = 4f, Padding = new Edges4(44f, 0f, Spacing.M, 4f),
            Children = [new TextEl(caption) { Size = 11f, Weight = 600, Color = Tok.TextTertiary }, CodeBlock.Create(text, copyable: true, fontSize: 12f)],
        };

        /// <summary>A dot only for Warning and ≥ Error; every other level holds the 6-DIP column with a blank spacer.</summary>
        static Element SeverityDot(WaveeLogLevel level) => level switch
        {
            WaveeLogLevel.Warning => InfoBadge.Dot(InfoBadgeSeverity.Caution),
            >= WaveeLogLevel.Error => InfoBadge.Dot(InfoBadgeSeverity.Critical),
            _ => new BoxEl { Width = 6f, Height = 6f, Shrink = 0f },
        };

        static Element RepeatBadge(int repeat) => new BoxEl
        {
            Padding = new Edges4(7f, 1f, 7f, 2f), Corners = CornerRadius4.All(Radii.Full), Fill = Tok.FillSubtleSecondary,
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
                Width = 58f, Height = 22f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = CornerRadius4.All(Radii.Full),
                Fill = color with { A = 0.12f }, BorderWidth = 1f, BorderColor = color with { A = 0.38f },
                Children = [new TextEl(level.ToString().ToUpperInvariant()) { Size = 10f, Weight = 800, Color = color }],
            };
        }

        // ── the footer: shown/total + Load more + the capture caption ────────────────────────────────────────────────

        Element Footer(LogViewResult result, bool live)
        {
            var kids = new List<Element>(3)
            {
                new TextEl(live ? Strings.Settings.Diagnostics.FooterLive(result.Shown, result.Total) : Strings.Settings.Diagnostics.FooterPast(result.Shown, result.Total))
                    { Size = 12f, Color = Tok.TextSecondary, Grow = 1f, MinWidth = 0f },
            };
            if (result.Truncated)
                kids.Add(HyperlinkButton.Create(Loc.Get(Strings.Settings.Diagnostics.LoadMore), () => _visibleLimit.Value = LogView.NextCap(_visibleLimit.Peek())));
            var min = Log.MinLevel;
            var file = LogCapturePolicy.EffectiveFileLevel(min, Log.FileMinLevel);
            kids.Add(new TextEl(Strings.Settings.Diagnostics.CaptureCaption(LevelName(min), LevelName(file))) { Size = 11f, Color = Tok.TextTertiary });
            return new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, Padding = new Edges4(Spacing.L, Spacing.S, Spacing.M, Spacing.S),
                Children = kids.ToArray(),
            };
        }

        static string LevelName(WaveeLogLevel level) => LogView.LevelNames[Math.Clamp((int)level, 0, LogView.LevelNames.Length - 1)];
    }

    // ══ 5. THE RUNTIME DIAGNOSTICS PAGE (ch 27 W23; 0.2.9 `PlaybackRuntimeDiagnosticsPage.cs`) ═════════════════════

    /// <summary>"Why is local playback not ready?" — the provisioner's own computed report, verbatim. The page adds no
    /// detection of its own, which is why Refresh can re-read it with no side effect. Every section states its own absence
    /// in prose; only Locate / Candidates / Verify drop out without a report — the status, modules, updates, the buttons
    /// and the caption always render.</summary>
    sealed class RuntimePageView : Component
    {
        readonly Signal<int> _refresh = new(0);

        public override Element Render()
        {
            var hooks = UseContext(InputHooks.Current);
            _ = _refresh.Value;
            var status = Setup.Runtime.Status.Value;
            var update = Notify.Update.Value;
            var diag = RuntimeReportSource?.Invoke();

            var body = new List<Element>(10) { CompiledInCard(diag), StatusSection(status) };
            if (diag is not null)
            {
                body.Add(Card(Loc.Get(Strings.Diagnostics.Runtime.Locate),
                    Row(Loc.Get(Strings.Diagnostics.Runtime.Outcome), diag.LocateOutcome.ToString()),
                    Row(Loc.Get(Strings.Diagnostics.Runtime.Reason), diag.LocateReason)));
                body.Add(CandidatesSection(diag));
                body.Add(VerifySection(diag));
            }
            body.Add(ModulesSection(ModulesReportSource?.Invoke()));
            body.Add(UpdatesSection(update, NotesReportSource?.Invoke()));
            body.Add(new BoxEl
            {
                Direction = 0, Gap = Spacing.S,
                Children =
                [
                    Button.Accent(Loc.Get(Strings.Diagnostics.Runtime.Copy), () =>
                    {
                        hooks.Clipboard?.SetText(BuildRuntimeReport(RuntimeReportSource?.Invoke(), Setup.Runtime.Status.Peek(), DateTimeOffset.Now, Log.FilePath));
                        Notify.Say(Loc.Get(Strings.Diagnostics.Runtime.Copied), InfoBarSeverity.Success);
                    }),
                    Button.Standard(Loc.Get(Strings.Settings.Diagnostics.OpenLogFolder), static () => OpenFolder(Path.GetDirectoryName(Log.FilePath ?? ""))),
                    Button.Standard(Loc.Get(Strings.Settings.Diagnostics.Refresh), () => _refresh.Value = _refresh.Peek() + 1),
                ],
            });
            body.Add(Caption(Loc.Get(Strings.Diagnostics.Runtime.Caption)));
            return PageFrame(Icons.MusicNote, Loc.Get(Strings.Playback.Runtime.DiagnosticsTitle), "playback-diagnostics", body);
        }

        /// <summary>THREE headline states: no report (Attention, "no playback session yet"), compiled in (Success), not
        /// compiled in (Critical, with the locate reason as the body).</summary>
        static Element CompiledInCard(RuntimeDiagnostics? diag)
        {
            if (diag is null)
                return Status(Icons.StatusInfo, Tok.SystemFillAttention, Loc.Get(Strings.Diagnostics.Runtime.NoSessionTitle), Loc.Get(Strings.Diagnostics.Runtime.NoSessionBody));
            return diag.CompiledIn
                ? Status(Icons.StatusSuccess, Tok.SystemFillSuccess, Loc.Get(Strings.Diagnostics.Runtime.CompiledInTitle), Loc.Get(Strings.Diagnostics.Runtime.CompiledInBody))
                : Status(Icons.StatusError, Tok.SystemFillCritical, Loc.Get(Strings.Diagnostics.Runtime.NotCompiledTitle), diag.LocateReason ?? "");
        }

        static Element StatusSection(Setup.RuntimeFacts s) => Card(Loc.Get(Strings.Diagnostics.Runtime.CurrentStatus),
            Row(Loc.Get(Strings.Diagnostics.Runtime.Outcome), s.IsReady ? "Ready" : s.Issue.ToString()),
            Row(Loc.Get(Strings.Diagnostics.Runtime.Pack), s.PackId),
            Row(Loc.Get(Strings.Diagnostics.Runtime.SpotifyVersion), s.Version),
            Row(Loc.Get(Strings.Diagnostics.Runtime.Architecture), s.Arch),
            Row(Loc.Get(Strings.Diagnostics.Runtime.RuntimePath), s.Location),
            Row(Loc.Get(Strings.Diagnostics.Runtime.SignatureTrust), s.Trust.ToString()));

        static Element CandidatesSection(RuntimeDiagnostics d)
        {
            string title = Loc.Get(Strings.Diagnostics.Runtime.Candidates);
            if (d.Candidates.Count == 0) return Card(title, Body(Loc.Get(Strings.Diagnostics.Runtime.NoCandidates)));
            var rows = new List<Element>(d.Candidates.Count * 3);
            for (int i = 0; i < d.Candidates.Count; i++)
            {
                var c = d.Candidates[i];
                if (i > 0) rows.Add(Separator(6f));
                rows.Add(new BoxEl
                {
                    Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center,
                    Children = [new TextEl(c.Source) { Size = 12f, Weight = 600, Color = Tok.TextPrimary }, Chip("Spotify.dll", c.DllPresent), Chip("playplay-runtime.json", c.ManifestPresent)],
                });
                rows.Add(new TextEl(string.IsNullOrWhiteSpace(c.RuntimeDir) ? Loc.Get(Strings.Diagnostics.Runtime.NoPath) : c.RuntimeDir)
                    { Size = 12f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap });
            }
            return Card(title, rows);
        }

        static Element VerifySection(RuntimeDiagnostics d)
        {
            string title = Loc.Get(Strings.Diagnostics.Runtime.Verify);
            if (d.VerifyNeverReached) return Card(title, Body(Loc.Get(Strings.Diagnostics.Runtime.VerifyNeverReached)));
            var rows = new List<Element>(9)
            {
                Row(Loc.Get(Strings.Diagnostics.Runtime.Outcome), d.VerifyOutcome?.ToString()),
                Row(Loc.Get(Strings.Diagnostics.Runtime.Detail), d.VerifyDetail),
                Row(Loc.Get(Strings.Diagnostics.Runtime.SignatureTrust), d.SignatureTrust?.ToString()),
            };
            if (d.Signature is { } sig)
            {
                rows.Add(Row(Loc.Get(Strings.Diagnostics.Runtime.Publisher), sig.Subject));
                rows.Add(Row(Loc.Get(Strings.Diagnostics.Runtime.Issuer), sig.Issuer));
                rows.Add(Row(Loc.Get(Strings.Diagnostics.Runtime.Thumbprint), sig.Thumbprint));
                rows.Add(Row(Loc.Get(Strings.Diagnostics.Runtime.Reason), sig.Reason));
                rows.Add(Row(Loc.Get(Strings.Diagnostics.Runtime.Valid), Stamp(sig.ValidFrom.ToLocalTime()) + "  →  " + Stamp(sig.ValidTo.ToLocalTime())));
                rows.Add(Row(Loc.Get(Strings.Diagnostics.Runtime.File), sig.FilePath));
            }
            return Card(title, rows);
        }

        /// <summary>One block per installed module, plus every refused directory with its reason. Retry for Faulted OR
        /// Crashed; the Requests / Latency / Last error / Status rows only when there is something behind them.</summary>
        static Element ModulesSection(ModulesReport? host)
        {
            string title = Loc.Get(Strings.Diagnostics.Runtime.Modules);
            if (host is null) return Card(title, Body(Loc.Get(Strings.Diagnostics.Runtime.NoModuleHost)));
            var inv = CultureInfo.InvariantCulture;
            var rows = new List<Element>(16);
            if (host.Installed.Count == 0) rows.Add(Body(Strings.Diagnostics.Runtime.NoModules(host.BundledRoot, host.UserRoot)));
            for (int i = 0; i < host.Installed.Count; i++)
            {
                var m = host.Installed[i];
                if (i > 0) rows.Add(Separator(6f));
                rows.Add(new BoxEl
                {
                    Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center,
                    Children =
                    [
                        new TextEl(m.DisplayName) { Size = 12f, Weight = 600, Color = Tok.TextPrimary },
                        Chip(Loc.Get(m.Bundled ? Strings.Diagnostics.Runtime.Bundled : Strings.Diagnostics.Runtime.Installed), present: true),
                        Chip(m.ProcessState ?? "Stopped", m.ProcessHealthy),
                    ],
                });
                rows.Add(Row(Loc.Get(Strings.Diagnostics.Runtime.ModuleId), m.Id));
                rows.Add(Row(Loc.Get(Strings.Diagnostics.Runtime.ModuleVersion), Strings.Diagnostics.Runtime.ModuleVersionValue(m.Version, m.ProtocolVersion.ToString(inv))));
                rows.Add(Row(Loc.Get(Strings.Diagnostics.Runtime.Publisher), m.Publisher));
                rows.Add(Row(Loc.Get(Strings.Diagnostics.Runtime.Directory), m.Directory));
                rows.Add(Row(Loc.Get(Strings.Diagnostics.Runtime.Process), m.ProcessId is { } pid
                    ? Strings.Diagnostics.Runtime.ProcessPid(pid.ToString(inv)) : Loc.Get(Strings.Diagnostics.Runtime.NotRunning)));
                rows.Add(Row(Loc.Get(Strings.Diagnostics.Runtime.Capabilities), m.Capabilities));
                if (m.HasStats)
                {
                    rows.Add(Row(Loc.Get(Strings.Diagnostics.Runtime.Requests),
                        Strings.Diagnostics.Runtime.RequestsValue(m.Requests.ToString(inv), m.Failures.ToString(inv), m.Restarts.ToString(inv))));
                    rows.Add(Row(Loc.Get(Strings.Diagnostics.Runtime.Latency), Strings.Diagnostics.Runtime.LatencyValue(m.P50Ms.ToString(inv), m.P95Ms.ToString(inv))));
                    if (m.LastError is { Length: > 0 } err) rows.Add(Row(Loc.Get(Strings.Diagnostics.Runtime.LastError), err));
                }
                if (m.StatusCard is { Length: > 0 } card) rows.Add(Row(Loc.Get(Strings.Diagnostics.Runtime.StatusRow), card));
                if (m.CanRetry && host.Retry is { } retry)
                {
                    string id = m.Id;
                    rows.Add(new BoxEl { Direction = 0, Children = [Button.Standard(Loc.Get(Strings.Diagnostics.Runtime.Retry), () => retry(id))] });
                }
            }
            if (host.Rejections.Count > 0)
            {
                rows.Add(Separator(8f));
                rows.Add(new TextEl(Loc.Get(Strings.Diagnostics.Runtime.Refused)) { Size = 12f, Weight = 600, Color = Tok.TextSecondary });
                foreach (var (dir, reason) in host.Rejections)
                {
                    rows.Add(new TextEl(dir) { Size = 12f, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap });
                    rows.Add(new TextEl(reason) { Size = 12f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap });
                }
            }
            return Card(title, rows);
        }

        /// <summary>The update pillar's receipts (it lives here: the same "which feed, which build, what did the OS do"
        /// questions, one thing to copy). "Repair auto-update" downloads the .appinstaller through the browser; the hint
        /// says so, because the link alone looks like it did nothing.</summary>
        static Element UpdatesSection(AppUpdateSnapshot snap, NotesReport? notes)
        {
            var me = Platform.Version;
            string feed = Update.Host.FeedUrl;
            var inv = CultureInfo.InvariantCulture;
            var rows = new List<Element>(14)
            {
                Row(Loc.Get(Strings.Diagnostics.Updates.Feed), feed),
                Row(Loc.Get(Strings.Diagnostics.Updates.Channel), me.Channel),
                Row(Loc.Get(Strings.Diagnostics.Updates.Version), me.Quad.Length > 0 ? me.Quad : me.SemVer),
                Row(Loc.Get(Strings.Diagnostics.Updates.State), snap.State.ToString()),
                Row(Loc.Get(Strings.Diagnostics.Updates.Associated), Loc.Get(snap.AutoUpdateAssociated ? Strings.Diagnostics.Updates.AssociatedYes : Strings.Diagnostics.Updates.AssociatedNo)),
                Row(Loc.Get(Strings.Diagnostics.Updates.LastChecked), snap.LastCheckedMs > 0 ? Stamp(DateTimeOffset.FromUnixTimeMilliseconds(snap.LastCheckedMs).ToLocalTime()) : null),
                Row(Loc.Get(Strings.Diagnostics.Updates.LastFailure), snap.Failure is { } fail
                    ? fail.Kind + "  ·  0x" + fail.HResult.ToString("X8", inv) + (fail.Message.Length > 0 ? "  ·  " + fail.Message : "") : null),
            };
            if (notes is not null)
            {
                rows.Add(Row(Loc.Get(Strings.Diagnostics.Updates.NotesSource), notes.LastSource));
                rows.Add(Row(Loc.Get(Strings.Diagnostics.Updates.NotesCache), notes.CacheRoot));
                rows.Add(Row(Loc.Get(Strings.Diagnostics.Updates.NotesEmbedded), notes.EmbeddedRoot));
                rows.Add(Row(Loc.Get(Strings.Diagnostics.Updates.NotesFeed), notes.FeedRelease));
                rows.Add(Row(Loc.Get(Strings.Diagnostics.Updates.NotesLastFetch), notes.LastFetchUtc is { } at
                    ? Stamp(at.ToLocalTime()) + (notes.LastFetchUrl.Length > 0 ? "  ·  " + notes.LastFetchUrl : "") + (notes.LastFetchStatus.Length > 0 ? "  ·  " + notes.LastFetchStatus : "")
                    : null));
                rows.Add(Row(Loc.Get(Strings.Diagnostics.Updates.NotesBudget),
                    Strings.Diagnostics.Updates.NotesBudgetValue(notes.IssueRequestsThisSession, notes.RateLimitRemaining)));
            }
            if (feed.Length > 0)
                rows.Add(new BoxEl
                {
                    Direction = 1, Gap = 2f, Margin = new Edges4(0f, 6f, 0f, 0f),
                    Children = [HyperlinkButton.Create(Loc.Get(Strings.Diagnostics.Updates.Repair), () => Update.Host.OpenUrl(feed)), Caption(Loc.Get(Strings.Diagnostics.Updates.RepairHint))],
                });
            return Card(Loc.Get(Strings.Diagnostics.Updates.Title), rows);
        }
    }

    // ══ 6. THE CONNECT DIAGNOSTICS PAGE (ch 27 W24; 0.2.9 `ConnectDiagnosticsPage.cs`) ═════════════════════════════

    /// <summary>"Who owns playback, and why does the bar say what it says" — a direct read of the ownership fold, the last
    /// cluster, the last put-state round trip and the local host. Owner / cluster / host state their absence in prose; the
    /// two put-state cards never do (six dashed rows each, by design). Re-renders on the owner, the session phase and the
    /// traces' version — never on the position.</summary>
    sealed class ConnectPageView : Component
    {
        public override Element Render()
        {
            _ = Playback.OwnerSignal.Value;
            _ = Playback.PhaseSignal.Value;
            _ = Connect.Version.Value;
            bool online = Spotify.Status.Value == Spotify.SessionPhase.Online;
            var snap = Playback.Snap();
            var offset = LocalOffset;

            var body = new List<Element>(6)
            {
                online ? OwnerCard(in snap.Own, offset) : Card(Loc.Get(Strings.Diagnostics.Connect.Owner), Body(Loc.Get(Strings.Diagnostics.Connect.NoSession))),
                Connect.LastCluster is { } cluster ? ClusterCard(in cluster, offset) : Card(Loc.Get(Strings.Diagnostics.Connect.LastCluster), Body(Loc.Get(Strings.Diagnostics.Connect.NoCluster))),
                PutCard(Connect.LastPut, offset),
                EchoCard(Connect.LastEcho, offset),
                HostCard(in snap),
                Caption(Loc.Get(Strings.Diagnostics.Connect.Caption)),
            };
            return PageFrame(Icons.Devices, Loc.Get(Strings.Nav.ConnectDiagnostics), "connect-diagnostics", body);
        }

        static Element R(string label, string? value) => Row(label, value, ConnectLabelWidth);

        static Element OwnerCard(in Playback.OwnerState o, TimeSpan offset)
        {
            var inv = CultureInfo.InvariantCulture;
            string state = o.Kind switch
            {
                Playback.Owner.Foreign => "Foreign · " + Playback.Devices.NameOf(o.Device),
                Playback.Owner.Nobody => "Nobody (" + o.Cause + ")",
                _ => "Us",
            };
            return Card(Loc.Get(Strings.Diagnostics.Connect.Owner),
                R(Loc.Get(Strings.Diagnostics.Connect.State), state),
                R(Loc.Get(Strings.Diagnostics.Connect.ClaimPhase), o.Claim.ToString()),
                R(Loc.Get(Strings.Diagnostics.Connect.ClaimId), o.ClaimId > 0 ? o.ClaimId.ToString(inv) : null),
                R(Loc.Get(Strings.Diagnostics.Connect.StartedAt), StampMs(o.ClaimStartedAtMs, offset)),
                R(Loc.Get(Strings.Diagnostics.Connect.FenceServerTs), StampMs(o.Fence, offset)),
                R(Loc.Get(Strings.Diagnostics.Connect.LastServerTs), StampMs(o.LastServerTs, offset)),
                R(Loc.Get(Strings.Diagnostics.Connect.LastSeen), o.LastSeenActive != 0
                    ? Playback.Devices.NameOf(o.LastSeenActive) + " @ " + StampMs(o.LastSeenServerTs, offset) : null));
        }

        static Element ClusterCard(in ClusterTrace c, TimeSpan offset)
        {
            var inv = CultureInfo.InvariantCulture;
            return Card(Loc.Get(Strings.Diagnostics.Connect.LastCluster),
                R(Loc.Get(Strings.Diagnostics.Connect.ActiveIdRaw), c.ActiveId),
                R(Loc.Get(Strings.Diagnostics.Connect.Origin), c.PutMsgId != 0 ? c.Origin + " #" + c.PutMsgId.ToString(inv) : c.Origin),
                R(Loc.Get(Strings.Diagnostics.Connect.UpdateReason), c.UpdateReason.ToString(inv)),
                R(Loc.Get(Strings.Diagnostics.Connect.ChangedDevices), c.ChangedDevices),
                R(Loc.Get(Strings.Diagnostics.Connect.ServerTs), StampMs(c.ServerTsMs, offset)),
                R(Loc.Get(Strings.Diagnostics.Connect.TrackUri), c.TrackUri),
                R(Loc.Get(Strings.Diagnostics.Connect.PlayingPaused), (c.IsPlaying ? "playing" : "not playing") + " / " + (c.IsPaused ? "paused" : "not paused")),
                R(Loc.Get(Strings.Diagnostics.Connect.Position), c.PositionAsOfMs.ToString(inv) + " ms"));
        }

        static Element PutCard(PutTrace p, TimeSpan offset) => Card(Loc.Get(Strings.Diagnostics.Connect.LastPut),
            R(Loc.Get(Strings.Diagnostics.Connect.MsgId), p.MsgId == 0 ? null : p.MsgId.ToString(CultureInfo.InvariantCulture)),
            R(Loc.Get(Strings.Diagnostics.Connect.Reason), p.Reason),
            R("is_active", p.IsActive.ToString()),
            R("started_playing_at", StampMs(p.StartedPlayingAtMs, offset)),
            R("has_been_playing_for", p.HasBeenPlayingForMs.ToString(CultureInfo.InvariantCulture) + " ms"),
            R(Loc.Get(Strings.Diagnostics.Connect.SentAt), StampMs(p.AtMs, offset)));

        static Element EchoCard(EchoTrace e, TimeSpan offset) => Card(Loc.Get(Strings.Diagnostics.Connect.LastEcho),
            R(Loc.Get(Strings.Diagnostics.Connect.MsgId), e.MsgId == 0 ? null : e.MsgId.ToString(CultureInfo.InvariantCulture)),
            R(Loc.Get(Strings.Diagnostics.Connect.ActiveId), e.ActiveId),
            R(Loc.Get(Strings.Diagnostics.Connect.ServerTs), StampMs(e.ServerTs, offset)),
            R(Loc.Get(Strings.Diagnostics.Connect.ClusterStartedAt), StampMs(e.ClusterStartedAtMs, offset)),
            R(Loc.Get(Strings.Diagnostics.Connect.Adopted), e.Adopted.ToString()),
            R(Loc.Get(Strings.Diagnostics.Connect.ReceivedAt), StampMs(e.AtMs, offset)));

        /// <summary>The local host: absent (prose) while a foreign device owns playback or nothing has loaded.</summary>
        static Element HostCard(in Playback.State s)
        {
            string title = Loc.Get(Strings.Diagnostics.Connect.Host);
            if (!s.HasCurrent || !s.RoutesLocal) return Card(title, Body(Loc.Get(Strings.Diagnostics.Connect.NoHost)));
            return Card(title,
                R(Loc.Get(Strings.Diagnostics.Connect.Playing), s.IsPlaying.ToString()),
                R(Loc.Get(Strings.Diagnostics.Connect.ClockValid), (s.PosQpc > 0).ToString()),
                R(Loc.Get(Strings.Diagnostics.Connect.Position), s.PosMs.ToString(CultureInfo.InvariantCulture) + " ms"));
        }
    }

    // ══ 7. THE LYRICS SOURCE INSPECTOR (A14 — moved out of the lyrics files by ch 22 §9 (d); ch 22 W17-W19b) ═══════════
    //
    // The answer to "these lyrics are out of sync — is that the provider's timings or our parser?". For the playing track
    // it shows (1) PROVIDERS: every source that ran, whether it was chosen and why not; (2) RAW: each payload exactly as
    // it arrived, copyable per payload; (3) PARSED: each candidate's document and the FINAL one the view got, with the
    // timing anomalies that matter for desync coloured. Everything it reads is `Lyrics.Diag`'s store; the one thing it
    // DOES is "Re-fetch from providers", because a cached answer carries no payload. A developer surface on a theme
    // PLATE (a ContentDialog card): theme text rungs and a monospace face, never the reading surface's ink. No context
    // menu, no drag, no shortcut. The pure half (the text, the inks, the cells) is `LyricsReport` in Diagnostics.cs; the
    // bundle writer is `SaveLyricsBundle` in Diagnostics.Host.cs.

    /// <summary>The inspector's two doors: <see cref="Open"/> (the dialog) and <see cref="Button()"/> (the rail header's
    /// developer-mode <c>&lt;/&gt;</c> glyph, mounted by <c>Rail.UI.cs</c>).</summary>
    public static class LyricsInspector
    {
        /// <summary>The WinUI ContentDialog maximum — a raw payload wants every DIP of it (ch 22 §3).</summary>
        public const float DialogWidth = 548f;

        /// <summary>Open the inspector for <paramref name="trackId"/> (the base62 id <c>Lyrics.Store.IdOf</c> keys the
        /// diagnostics store with; "" = nothing playing). Reports only: Close is the one command and the default.</summary>
        public static void Open(IOverlayService overlay, string trackId) => ContentDialog.Show(overlay, d =>
        {
            d.Title = Loc.Get(Strings.Player.LyricsInspector);
            d.DialogWidth = DialogWidth;
            d.PrimaryText = "";                        // "" hides the primary (null would show the localized OK)
            d.CloseText = Loc.Get(Strings.Common.Close);
            d.DefaultButton = ContentDialog.DefaultBtn.Close;
            d.Content = Embed.Comp(() => new LyricsInspectorBody(trackId));
        });

        /// <summary>The rail header's <c>&lt;/&gt;</c> glyph for the playing track. Composes itself away unless
        /// developer mode is on, and follows a flip live.</summary>
        // MOUNT POINT (stage B contract)
        public static Element Button() => Button(static () => CurrentTrackId());

        /// <summary>The same glyph over a caller-chosen track. The id is read at CLICK time through
        /// <paramref name="currentTrackId"/>, so the glyph never re-renders on a track change.</summary>
        public static Element Button(Func<string> currentTrackId) => Embed.Comp(() => new LyricsInspectorButton(currentTrackId));

        /// <summary>The playing row's diagnostics key, or "" when nothing (or an episode) is playing. A <c>Peek</c>:
        /// called at click time, never in a render.</summary>
        public static string CurrentTrackId()
        {
            var row = Playback.Current.Peek();
            return row.Kind == EntityKind.Track ? Lyrics.Store.IdOf(new Track(row.Slot)) : "";
        }
    }

    /// <summary>The header glyph. Its own component so ONLY it subscribes the settings epoch: a developer-mode flip
    /// mounts or removes it without re-rendering the rail header. The track getter is a mount seed (a thunk read at
    /// click time), not changing caller data.</summary>
    sealed class LyricsInspectorButton(Func<string> currentTrackId) : Component
    {
        public override Element Render()
        {
            var overlay = UseContext(Overlay.Service);
            _ = Platform.SettingsChanged.Value;   // subscribe → a Developer-mode flip composes the glyph in/out live
            if (!Platform.Settings.Get(Platform.Keys.DeveloperMode)) return new BoxEl { Visible = false };
            return Rail.HeaderButton(Icons.Code, Loc.Get(Strings.Player.InspectLyrics), () =>
            {
                if (Controls.IsNullOverlay(overlay)) return;
                LyricsInspector.Open(overlay, currentTrackId());
            });
        }
    }

    /// <summary>The dialog body (548 card, 492 content). The track id is the dialog's IDENTITY — one open, one track —
    /// so it is a mount seed; everything that changes is a signal on this instance.</summary>
    sealed class LyricsInspectorBody(string trackId) : Component
    {
        const float ContentW = LyricsInspector.DialogWidth - 56f;   // 548 − 2×24 card padding − the scrollbar lane = 492
        const string Mono = "Cascadia Code";

        // The inspector's own severity inks (0.2.9's, theme-invariant on a theme plate — they read on both).
        static readonly ColorF Good = new(0.30f, 0.78f, 0.45f, 1f);
        static readonly ColorF Warn = new(0.92f, 0.70f, 0.25f, 1f);
        static readonly ColorF Bad = new(0.90f, 0.35f, 0.38f, 1f);
        static readonly ColorF Dim = new(0.40f, 0.42f, 0.50f, 1f);
        static readonly ColorF Grey = new(0.55f, 0.57f, 0.62f, 1f);

        static string[]? s_tabs;   // the launch locale is fixed for the process, so the labels resolve once

        readonly string _trackId = trackId;
        readonly Signal<int> _tab = new(0);           // 0 providers · 1 raw · 2 parsed
        readonly Signal<string> _focus = new("");     // the provider the raw/parsed tabs show ("" = the final document)
        readonly Signal<int> _epoch = new(0);         // bump to re-read the static diagnostics store
        readonly Signal<bool> _syllables = new(false);
        readonly Signal<bool> _refetching = new(false);
        readonly Signal<bool> _saving = new(false);
        readonly Signal<string> _status = new("");    // the amber status line; "" = absent

        public override Element Render()
        {
            var hooks = UseContext(InputHooks.Current);
            var post = UsePost();
            _ = _epoch.Value;   // Refresh and a finished re-fetch bump it: re-read the store

            // W19b NO TRACK: the note is the ONLY child — no toolbar, no tabs.
            if (_trackId.Length == 0) return Body(Note(Loc.Get(Strings.Diagnostics.Inspector.NoTrack)));

            var report = Lyrics.Diag.ForTrack(_trackId);
            var insp = Lyrics.Diag.InspectionFor(_trackId);
            Element tab = _tab.Value switch
            {
                1 => RawTab(hooks, insp),
                2 => ParsedTab(hooks, insp),
                _ => ProvidersTab(report, insp),
            };
            s_tabs ??=
            [
                Loc.Get(Strings.Diagnostics.Inspector.TabProviders),
                Loc.Get(Strings.Diagnostics.Inspector.TabRaw),
                Loc.Get(Strings.Diagnostics.Inspector.TabParsed),
            ];
            return Body(Toolbar(hooks, post, report, insp), SelectorBar.Create(s_tabs, _tab), tab);
        }

        static BoxEl Body(params Element[] kids) => new() { Direction = 1, Gap = 12f, Width = ContentW, Children = kids };

        // ── the toolbar ──────────────────────────────────────────────────────────────────────────────────────────────

        Element Toolbar(InputHooks hooks, Action<Action> post, Lyrics.SearchReport? report, Lyrics.Inspection? insp)
        {
            bool refetching = _refetching.Value, busy = refetching || _saving.Value;
            var kids = new List<Element>(3)
            {
                Row(8f,
                    Standard(Loc.Get(Strings.Diagnostics.Inspector.CopyReport), () =>
                    {
                        hooks.Clipboard?.SetText(LyricsReport.BuildReport(_trackId, report, insp));
                        Notify.Say(Loc.Get(Strings.Lyrics.Inspector.ReportCopied), InfoBarSeverity.Success);
                    }),
                    // The report is what fits in a clipboard; the BUNDLE is what an investigation needs — every payload
                    // byte-for-byte and every parse as a TSV, in a folder that can be diffed or replayed as a fixture.
                    Standard(Loc.Get(busy ? Strings.Diagnostics.Inspector.Working : Strings.Diagnostics.Inspector.SaveBundle),
                        () => SaveBundle(post, report, insp), enabled: !busy),
                    Standard(Loc.Get(Strings.Diagnostics.Inspector.Refresh), () => _epoch.Value = _epoch.Peek() + 1),
                    FluentGpu.Controls.Button.Accent(
                        Loc.Get(refetching ? Strings.Diagnostics.Inspector.Refetching : Strings.Diagnostics.Inspector.Refetch),
                        () => Refetch(post, thenSave: false), isEnabled: !busy)),
                Caption(Loc.Get(Strings.Lyrics.Inspector.RefetchNote)),
            };
            if (_status.Value is { Length: > 0 } status)
                kids.Add(new TextEl(status) { Size = 12f, LineHeight = 16f, Color = Warn, Wrap = TextWrap.Wrap });
            return new BoxEl { Direction = 1, Gap = 6f, Children = kids.ToArray() };
        }

        /// <summary>Save, fetching FIRST when there is nothing to save: a cache hit carries no payload, so a bundle written
        /// from one is the useless artifact this button exists to avoid — chain it, never hand over an empty folder.</summary>
        void SaveBundle(Action<Action> post, Lyrics.SearchReport? report, Lyrics.Inspection? insp)
        {
            if (insp is null || insp.Raw.Count == 0) Refetch(post, thenSave: true);
            else WriteBundle(post, report, insp);
        }

        void WriteBundle(Action<Action> post, Lyrics.SearchReport? report, Lyrics.Inspection? insp)
        {
            if (_saving.Peek()) return;
            _saving.Value = true;
            string trackId = _trackId;
            int payloads = insp?.Raw.Count ?? 0;
            _ = Task.Run(() =>
            {
                string? folder = SaveLyricsBundle(trackId, report, insp, out string error);
                post(() => BundleLanded(folder, error, payloads));
            });
        }

        void BundleLanded(string? folder, string error, int payloads)
        {
            _saving.Value = false;
            if (folder is null)
            {
                _status.Value = error.Length > 0
                    ? Strings.Diagnostics.Inspector.SaveFailed(error)
                    : Loc.Get(Strings.Diagnostics.Inspector.NothingToSave);
                return;
            }
            // Say so LOUDLY rather than handing over a folder that silently holds no evidence.
            _status.Value = payloads == 0
                ? Strings.Diagnostics.Inspector.SavedNoPayloads(folder)
                : Strings.Diagnostics.Inspector.SavedPayloads(payloads, folder);
            Notify.Say(Loc.Get(Strings.Lyrics.Inspector.EvidenceSaved), InfoBarSeverity.Success);
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + folder + "\"") { UseShellExecute = true }); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                Log.Warn(Lyrics.Diag.Category, "the lyrics evidence folder could not be opened", ex);
            }
        }

        /// <summary>Drop this track from the aggregator's memory AND disk caches and run the fan-out again, straight on the
        /// provider: the store republishes the inspection, and the lyrics already on screen are left alone. A session
        /// with no provider (offline, signed out, the fake backend) says so in the amber line — never a toast.</summary>
        void Refetch(Action<Action> post, bool thenSave)
        {
            if (_refetching.Peek() || _saving.Peek()) return;
            if (Lyrics.Store.Provider is not { } provider)
            {
                _status.Value = Loc.Get(Strings.Diagnostics.Inspector.NoRefetch);
                return;
            }
            _refetching.Value = true;
            _status.Value = thenSave ? Loc.Get(Strings.Diagnostics.Inspector.RefetchingFirst) : "";
            _ = RefetchAsync(provider, _trackId, post, thenSave);
        }

        async Task RefetchAsync(Lyrics.Aggregator provider, string trackId, Action<Action> post, bool thenSave)
        {
            string? error = null;
            try { await Task.Run(() => provider.RefetchAsync(trackId)).ConfigureAwait(false); }
            catch (Exception e) { error = e.GetType().Name + ": " + e.Message; }   // reported in the status line, not rethrown
            post(() =>
            {
                _refetching.Value = false;
                _epoch.Value = _epoch.Peek() + 1;
                if (error is not null) { _status.Value = Strings.Diagnostics.Inspector.RefetchFailed(error); return; }
                _status.Value = "";
                // Re-read the store rather than the snapshot the click closed over: the fetch is what just filled it.
                if (thenSave) WriteBundle(post, Lyrics.Diag.ForTrack(trackId), Lyrics.Diag.InspectionFor(trackId));
            });
        }

        // ── 1. providers (W17) ───────────────────────────────────────────────────────────────────────────────────────

        Element ProvidersTab(Lyrics.SearchReport? report, Lyrics.Inspection? insp)
        {
            var kids = new List<Element>(8);
            if (report is null)
            {
                kids.Add(Note(Loc.Get(Strings.Diagnostics.Inspector.NoReport)));
                if (insp?.Note is { Length: > 0 } onlyNote) kids.Add(Caption(onlyNote));
                return Column(10f, kids);
            }

            kids.Add(new TextEl(report.Summary) { Size = 13f, LineHeight = 18f, Weight = 600, Color = Tok.AccentTextPrimary, Wrap = TextWrap.Wrap });
            kids.Add(new TextEl(Strings.Diagnostics.Inspector.TitleLine(
                    string.IsNullOrWhiteSpace(report.Title) ? Loc.Get(Strings.Diagnostics.Inspector.NoTitle) : report.Title,
                    string.IsNullOrWhiteSpace(report.Artist) ? Loc.Get(Strings.Lyrics.Debug.NoArtist) : report.Artist))
                { Size = 12f, LineHeight = 16f, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap });
            kids.Add(Caption(LyricsReport.IdentityLine(_trackId, report)));
            if (insp?.Note is { Length: > 0 } note) kids.Add(Caption(note));
            kids.Add(Rule());

            double winnerScore = LyricsReport.WinnerScore(report);
            if (report.Sources.Count == 0) kids.Add(Note(Loc.Get(Strings.Diagnostics.Inspector.NoSourceRan)));
            foreach (var t in report.Sources) kids.Add(ProviderCard(t, insp, winnerScore));
            return Column(10f, kids);
        }

        Element ProviderCard(Lyrics.SourceTrace t, Lyrics.Inspection? insp, double winnerScore)
        {
            ColorF dot = t.Outcome switch
            {
                Lyrics.Outcome.Hit => Good,
                Lyrics.Outcome.Timeout => Warn,
                Lyrics.Outcome.Error => Bad,
                Lyrics.Outcome.Skipped => Dim,
                _ => Grey,
            };
            int rawCount = LyricsReport.RawCount(insp, t.SourceId);
            var parsed = LyricsReport.CandidateFor(insp, t.SourceId);
            string id = t.SourceId;

            var rows = new List<Element>(6)
            {
                Row(8f,
                    new BoxEl { Width = 8f, Height = 8f, Corners = Radii.Circle(8f), Fill = dot, AlignSelf = FlexAlign.Center, Shrink = 0f },
                    new TextEl(t.SourceId) { Size = 13f, LineHeight = 18f, Weight = 700, Color = t.Winner ? Tok.AccentTextPrimary : Tok.TextPrimary },
                    new TextEl(LyricsReport.OutcomeLine(t)) { Size = 11f, LineHeight = 18f, Weight = 600, Color = Tok.TextTertiary, Grow = 1f, MinWidth = 0f }),
                new TextEl(LyricsReport.Verdict(t, winnerScore))
                    { Size = 12f, LineHeight = 16f, Wrap = TextWrap.Wrap, Color = t.Winner ? Good : Tok.TextSecondary },
            };
            if (t.Detail.Length > 0) rows.Add(Caption(t.Detail));
            if (parsed is not null)
            {
                rows.Add(Caption(LyricsReport.ParsedLine(parsed)));
                string timing = Lyrics.Timing.Describe(parsed.Document);
                rows.Add(new TextEl("timing: " + timing)
                {
                    Size = 11f, LineHeight = 15f, Wrap = TextWrap.Wrap,
                    Color = timing.StartsWith("clean", StringComparison.Ordinal) ? Tok.TextTertiary : Warn,
                });
            }
            // Each switches tab AND focus, and is DISABLED (and relabelled) when there is nothing behind it.
            rows.Add(Row(6f,
                Standard(rawCount > 0 ? Strings.Lyrics.Inspector.Raw(rawCount) : Loc.Get(Strings.Lyrics.Inspector.RawNone),
                    () => { _focus.Value = id; _tab.Value = 1; }, enabled: rawCount > 0),
                Standard(Loc.Get(parsed is not null ? Strings.Diagnostics.Inspector.TabParsed : Strings.Diagnostics.Inspector.ParsedNone),
                    () => { _focus.Value = id; _tab.Value = 2; }, enabled: parsed is not null)));

            return new BoxEl
            {
                Direction = 1, Gap = 5f, Padding = Edges4.All(10f), Corners = CornerRadius4.All(6f),
                Fill = Tok.FillSubtleSecondary, BorderWidth = 1f, BorderColor = t.Winner ? Tok.AccentDefault : Tok.StrokeCardDefault,
                Children = rows.ToArray(),
            };
        }

        // ── 2. raw (W18) ─────────────────────────────────────────────────────────────────────────────────────────────

        Element RawTab(InputHooks hooks, Lyrics.Inspection? insp)
        {
            if (insp is null || insp.Raw.Count == 0)
                return FluentGpu.Dsl.Ui.VStack(10f,
                    Note(Loc.Get(Strings.Diagnostics.Inspector.NoPayload)),
                    Caption(insp?.Note ?? Loc.Get(Strings.Diagnostics.Inspector.NothingRecorded)),
                    Caption(Loc.Get(Strings.Lyrics.Inspector.PayloadsNote)));

            var sources = LyricsReport.RawSources(insp);
            string focus = _focus.Value;
            if (!sources.Contains(focus)) focus = sources[0];   // a focus from another tab may name a source with no payload

            var chips = new Element[sources.Count];
            for (int i = 0; i < sources.Count; i++)
            {
                string id = sources[i];
                chips[i] = FluentGpu.Dsl.Ui.Pill(id + " (" + LyricsReport.RawCount(insp, id).ToString(System.Globalization.CultureInfo.InvariantCulture) + ")",
                    StringComparer.Ordinal.Equals(id, focus), () => _focus.Value = id);
            }
            var kids = new List<Element>(4) { FluentGpu.Dsl.Ui.Wrap(6f, chips) };
            foreach (var payload in insp.Raw)
                if (StringComparer.Ordinal.Equals(payload.SourceId, focus)) kids.Add(PayloadCard(hooks, payload));
            return Column(10f, kids);
        }

        static Element PayloadCard(InputHooks hooks, Lyrics.RawPayload p)
        {
            // The ON-SCREEN cap; Copy always hands over everything that was captured.
            string shown = p.Text.Length <= LyricsReport.OnScreenRawChars ? p.Text : p.Text[..LyricsReport.OnScreenRawChars];
            return new BoxEl
            {
                Direction = 1, Gap = 6f, Padding = Edges4.All(10f), Corners = CornerRadius4.All(6f),
                Fill = Tok.FillControlSecondary, BorderWidth = 1f, BorderColor = Tok.StrokeControlDefault,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 1, Gap = 4f,
                        Children =
                        [
                            new TextEl(p.Label) { Size = 11f, LineHeight = 15f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, FontFamily = Mono },
                            Row(8f,
                                Caption(LyricsReport.PayloadCaption(p, shown.Length)) with { Grow = 1f, MinWidth = 0f },
                                Standard(Loc.Get(Strings.Diagnostics.Inspector.Copy), () =>
                                {
                                    hooks.Clipboard?.SetText(p.Text);
                                    Notify.Say(Strings.Diagnostics.Inspector.PayloadCopied(p.SourceId), InfoBarSeverity.Success);
                                })),
                        ],
                    },
                    Rule(),
                    new TextEl(shown) { Size = 11f, LineHeight = 15f, FontFamily = Mono, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap },
                ],
            };
        }

        // ── 3. parsed (W19) ──────────────────────────────────────────────────────────────────────────────────────────

        Element ParsedTab(InputHooks hooks, Lyrics.Inspection? insp)
        {
            if (insp is null || (insp.Final is null && insp.Candidates.Count == 0))
                return FluentGpu.Dsl.Ui.VStack(10f,
                    Note(Loc.Get(Strings.Diagnostics.Inspector.NoParsed)),
                    Caption(insp?.Note ?? Loc.Get(Strings.Diagnostics.Inspector.NothingRecorded)));

            // A focus carried over from Providers/Raw may name a source that produced no DOCUMENT. Resolve it once so the
            // chip highlight and the body can never disagree about what is on screen.
            string focus = _focus.Value;
            var cand = LyricsReport.CandidateFor(insp, focus);
            Lyrics.Doc? doc = cand?.Document ?? insp.Final;
            string who = cand is null ? "final" : focus;

            var chips = new List<Element>(insp.Candidates.Count + 1);
            if (insp.Final is not null)
                chips.Add(FluentGpu.Dsl.Ui.Pill(Loc.Get(Strings.Diagnostics.Inspector.FinalChip), cand is null, () => _focus.Value = ""));
            foreach (var c in insp.Candidates)
            {
                string id = c.SourceId;
                chips.Add(FluentGpu.Dsl.Ui.Pill(id, cand is not null && StringComparer.Ordinal.Equals(id, focus), () => _focus.Value = id));
            }
            var kids = new List<Element>(8) { FluentGpu.Dsl.Ui.Wrap(6f, chips.ToArray()) };
            if (doc is null)
            {
                kids.Add(Note(Loc.Get(Strings.Diagnostics.Inspector.NoDocument)));
                return Column(10f, kids);
            }

            bool showSyllables = _syllables.Value;
            // Only the FINAL line claims to be what the UI got; a candidate is the parse BEFORE the offset correction.
            kids.Add(new TextEl(cand is null
                    ? Loc.Get(Strings.Diagnostics.Inspector.FinalDescription)
                    : Strings.Diagnostics.Inspector.CandidateDescription(who))
                { Size = 12f, LineHeight = 16f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap });
            kids.Add(Caption(LyricsReport.DocMeta(doc, who)));
            string anomalies = Lyrics.Timing.Describe(doc);
            kids.Add(new TextEl("timing check: " + anomalies)
            {
                Size = 12f, LineHeight = 16f, Wrap = TextWrap.Wrap,
                Color = anomalies.StartsWith("clean", StringComparison.Ordinal) ? Good : Warn,
            });
            Lyrics.Doc d = doc;   // a non-null local the Copy thunk captures (a `var` would re-read as nullable inside it)
            kids.Add(Row(6f,
                Standard(Loc.Get(showSyllables ? Strings.Diagnostics.Inspector.HideSyllables : Strings.Diagnostics.Inspector.ShowSyllables),
                    () => _syllables.Value = !_syllables.Peek()),
                Standard(Loc.Get(Strings.Diagnostics.Inspector.CopyParsed), () =>
                {
                    hooks.Clipboard?.SetText(LyricsReport.BuildParsed(who, d));
                    Notify.Say(Loc.Get(Strings.Lyrics.Inspector.ParsedCopied), InfoBarSeverity.Success);
                })));
            kids.Add(Rule());

            int count = Math.Min(doc.Lines.Count, LyricsReport.OnScreenLines);
            for (int i = 0; i < count; i++) kids.Add(LineRow(doc, i, showSyllables));
            if (doc.Lines.Count > count)
                kids.Add(Caption(Strings.Diagnostics.Inspector.MoreLines(doc.Lines.Count - count)));
            return Column(8f, kids);
        }

        static Element LineRow(Lyrics.Doc doc, int i, bool showSyllables)
        {
            var l = doc.Lines[i];
            var check = LyricsReport.Check(doc, i);
            ColorF timeInk = check.Time switch
            {
                LyricsReport.TimeInk.Bad => Bad,
                LyricsReport.TimeInk.Warn => Warn,
                _ => Tok.TextSecondary,
            };
            bool blank = l.Text.Length == 0;
            var kids = new List<Element>(4)
            {
                Row(8f,
                    new TextEl(i.ToString(System.Globalization.CultureInfo.InvariantCulture))
                        { Size = 11f, LineHeight = 16f, FontFamily = Mono, Color = Tok.TextTertiary, Width = 26f, Shrink = 0f },
                    new TextEl(LyricsReport.TimeCell(l)) { Size = 11f, LineHeight = 16f, FontFamily = Mono, Color = timeInk, Width = 130f, Shrink = 0f },
                    // The DURATION spelled out: a squashed line is invisible in two timestamps and obvious as "217ms".
                    new TextEl(LyricsReport.DurationCell(l))
                        { Size = 11f, LineHeight = 16f, FontFamily = Mono, Color = check.Squashed ? Warn : Tok.TextTertiary, Width = 52f, Shrink = 0f },
                    new TextEl(blank ? Loc.Get(Strings.Diagnostics.Inspector.BlankLine) : l.Text)
                    {
                        Size = 12f, LineHeight = 16f, Wrap = TextWrap.Wrap, Grow = 1f, MinWidth = 0f,
                        Color = blank ? Tok.TextTertiary : Tok.TextPrimary,
                    }),
            };
            if (l.Translation is { Length: > 0 } tr) kids.Add(SubLine("[tr] " + tr));
            if (l.Romanization is { Length: > 0 } ro) kids.Add(SubLine("[ro] " + ro));
            if (showSyllables && l.Syllables.Count > 0)
                kids.Add(new TextEl(LyricsReport.SyllableStrip(l))
                {
                    Size = 10.5f, LineHeight = 14f, FontFamily = Mono, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap,
                    Margin = new Edges4(34f, 0f, 0f, 0f),
                });
            return new BoxEl { Direction = 1, Gap = 2f, Children = kids.ToArray() };
        }

        // ── small helpers ────────────────────────────────────────────────────────────────────────────────────────────

        static BoxEl Row(float gap, params Element[] kids) => FluentGpu.Dsl.Ui.HStack(gap, kids);

        static BoxEl Column(float gap, List<Element> kids) => FluentGpu.Dsl.Ui.VStack(gap, kids.ToArray());

        static BoxEl Standard(string label, Action onClick, bool enabled = true)
            => FluentGpu.Controls.Button.Standard(label, onClick, isEnabled: enabled);

        static BoxEl Rule() => new() { Height = 1f, Fill = Tok.StrokeCardDefault };

        static TextEl SubLine(string text) => new(text)
        {
            Size = 11f, LineHeight = 15f, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap, Margin = new Edges4(34f, 0f, 0f, 0f),
        };

        static TextEl Note(string text) => new(text) { Size = 12f, LineHeight = 17f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap };

        static TextEl Caption(string text) => new(text) { Size = 11f, LineHeight = 15f, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap };
    }
}
