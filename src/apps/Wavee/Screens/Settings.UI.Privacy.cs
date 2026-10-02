// ── Screens/Settings.UI.Privacy.cs ─────────────────────────────────────────────────────────────────────────────────
// the Privacy & diagnostics tab: Privacy (what leaves this PC, the crash-report mode with its memory-snapshot switch,
// the crash-service card, the policy link) · Crash reports (ONE Saved reports expander: the send-queue InfoBar, a row
// per report with a "…" menu, Open folder / Delete all) · Logs (the viewer card with live counts, the two detail
// levels, the log-file rule, Report a problem) · Tools (Playback runtime, Spotify Connect, Realtime capture) ·
// Developer (developer mode and the three items it unlocks)
//
// Role: UI
// Owner: R
// Spec: docs/plans/wavee/privacy-diagnostics-tab-implementation.md §3.1, §5.1, §5.3-§5.6 — the tab replaces Settings ›
// Logs and absorbs General's Privacy & diagnostics and Developer groups; the decisions it makes are in
// `Settings.Privacy.cs` (`PrivacyRules`, engine-free and tested).
//
// THE SHAPE: `PrivacyDiagnosticsTab()` is a static body re-run on the page's own epoch (`Bump`), like every other
// tab. A group that must follow something the page does not bump — the reports folder, the send outbox, the log ring,
// the logger's levels, the developer signals — is its own component with its own subscriptions, so the page never
// re-renders for them. Every expander starts COLLAPSED (the header answers); every destructive action confirms
// through `Controls.Confirm`; every toast goes through `Notify.Say`.

using System.Globalization;
using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;
using PvDev = Wavee.Strings.Settings.Privacy.Developer;
using PvFacts = Wavee.Strings.Settings.Privacy.Facts;
using PvLogs = Wavee.Strings.Settings.Privacy.Logs;
using PvReports = Wavee.Strings.Settings.Privacy.Reports;
using PvTools = Wavee.Strings.Settings.Privacy.Tools;

namespace Wavee;

public static partial class Settings
{
    // ══ 1. THE TAB ════════════════════════════════════════════════════════════════════════════════════════════════════

    private static partial Element PrivacyDiagnosticsTab() => TabStack(
        SectionHeader(Loc.Get(PvFacts.Title), SectionGlyph(Tab.PrivacyDiagnostics, "Privacy"), Loc.Get(PvFacts.Subtitle)),
        WhatLeavesExpander(),
        CrashReportsExpander(),
        Embed.Comp(static () => new CrashServiceCard()) with { Key = "privacy.crash-service" },
        PrivacyPolicyCard(),

        SectionHeader(Loc.Get(PvReports.Title), SectionGlyph(Tab.PrivacyDiagnostics, "Crash reports"), Loc.Get(PvReports.Subtitle)),
        Embed.Comp(static () => new SavedReportsGroup()) with { Key = "privacy.reports" },

        SectionHeader(Loc.Get(PvLogs.Title), SectionGlyph(Tab.PrivacyDiagnostics, "Logs"), Loc.Get(PvLogs.Subtitle)),
        Embed.Comp(static () => new LogViewerCard()) with { Key = "privacy.log-viewer" },
        Embed.Comp(static () => new DetailLevelGroup()) with { Key = "privacy.detail-level" },
        LogFilesCard(),
        ReportProblemCard(),

        SectionHeader(Loc.Get(PvTools.Title), SectionGlyph(Tab.PrivacyDiagnostics, "Tools"), Loc.Get(PvTools.Subtitle)),
        ToolLinkCard(PvTools.PlaybackRuntime, PvTools.PlaybackRuntimeSub, "playbackRuntime", Shell.RouteKind.PlaybackDiagnostics),
        ToolLinkCard(PvTools.Connect, PvTools.ConnectSub, "connectDiagnostics", Shell.RouteKind.ConnectDiagnostics),
        RealtimeCaptureExpander(),

        SectionHeader(Loc.Get(PvDev.Title), SectionGlyph(Tab.PrivacyDiagnostics, "Developer"), Loc.Get(PvDev.Subtitle)),
        Embed.Comp(static () => new DeveloperGroup()) with { Key = "privacy.developer" });

    // ══ 2. PRIVACY ════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The collapsed answer is "No telemetry"; inside, the four places anything leaves this PC and why. Items
    /// carry no control — they are facts.</summary>
    static Element WhatLeavesExpander() => SettingsExpander.Create(new SettingsExpander.Options
    {
        Header = Loc.Get(PvFacts.WhatLeaves),
        Description = Loc.Get(PvFacts.WhatLeavesSub),
        HeaderIcon = RowGlyph(Tab.PrivacyDiagnostics, "whatLeaves"),
        Content = ValueTag(Loc.Get(PvFacts.NoTelemetry)),
        Items =
        [
            Item(Loc.Get(PvFacts.Spotify), Loc.Get(PvFacts.SpotifySub)),
            Item(Loc.Get(PvFacts.Lyrics), Loc.Get(PvFacts.LyricsSub)),
            Item(Loc.Get(PvFacts.Github), Loc.Get(PvFacts.GithubSub)),
            Item(Loc.Get(PvFacts.CrashService), Loc.Get(PvFacts.CrashServiceSub)),
        ],
        ItemsFooter = ExpanderFooter(FooterNote(Loc.Get(PvFacts.Footer))),
    }) with { Key = "privacy.facts" };

    /// <summary>Off / Ask each time / Automatic as three left-aligned radio items (D3: one <c>RadioButtons</c> group has
    /// only a group-wide enabled flag, and Automatic alone must be disabled with its reason), then the memory-snapshot
    /// switch (live only while reporting is on) and what a report contains. The header tag is the EFFECTIVE mode — a
    /// stored Automatic on a build that cannot send behaves as, and shows as, Ask each time.</summary>
    static Element CrashReportsExpander()
    {
        var stored = (Crash.Reporting)Platform.Settings.Get(Platform.Keys.CrashReporting);
        bool configured = Crash.Uploader.Configured;
        var effective = Crash.ConsentPolicy.Effective(stored, configured);
        bool reporting = effective != Crash.Reporting.Off;
        return SettingsExpander.Create(new SettingsExpander.Options
        {
            Header = Loc.Get(PvFacts.CrashReports),
            Description = Loc.Get(PvFacts.CrashReportsSub),
            HeaderIcon = RowGlyph(Tab.PrivacyDiagnostics, "crashReports"),
            Content = ValueTag(Loc.Get(PrivacyRules.ModeKey(effective))),
            Items =
            [
                ModeItem(Crash.Reporting.Off, effective, Loc.Get(PvFacts.ModeOff), Loc.Get(PvFacts.ModeOffSub), enabled: true),
                ModeItem(Crash.Reporting.Ask, effective, Loc.Get(PvFacts.ModeAsk), Loc.Get(PvFacts.ModeAskSub), enabled: true),
                ModeItem(Crash.Reporting.Auto, effective, Loc.Get(PvFacts.ModeAuto),
                    Loc.Get(configured ? PvFacts.ModeAutoSub : PvFacts.ModeAutoNeedsService), enabled: configured),
                Item(Loc.Get(PvFacts.IncludeDump), Loc.Get(PvFacts.IncludeDumpSub),
                    Toggle(Platform.Keys.CrashIncludeDump, isEnabled: reporting),
                    isEnabled: reporting, icon: RowGlyph(Tab.PrivacyDiagnostics, "crashDump")),
                Item(Loc.Get(PvFacts.Contents), Loc.Get(PvFacts.ContentsSub), icon: RowGlyph(Tab.PrivacyDiagnostics, "crashContents")),
            ],
        }) with { Key = "privacy.crash-reports" };
    }

    /// <summary>One mode: a radio and an indented caption, left-aligned so the card renders ONLY the content
    /// (<c>SettingsCard.ContentAlignment.Left</c>). Disabled, the caption says why.</summary>
    static Element ModeItem(Crash.Reporting mode, Crash.Reporting current, string label, string sub, bool enabled)
        => Item("", null, new BoxEl
        {
            Direction = 1, Gap = Spacing.XXS, MinWidth = 0f,
            Children =
            [
                RadioButton.Create(label, isSelected: current == mode, onChange: () => SetCrashMode(mode), isEnabled: enabled),
                Caption(sub) with
                {
                    Color = enabled ? Tok.TextSecondary : Tok.TextDisabled,
                    Margin = new Edges4(28f, 0f, 0f, 0f), Wrap = TextWrap.Wrap, MaxLines = 2,
                },
            ],
        }, SettingsCard.ContentAlignment.Left, isEnabled: enabled);

    /// <summary>Compared against the STORED mode (not the effective one), so choosing Ask over a stored-but-unusable
    /// Automatic really writes Ask.</summary>
    static void SetCrashMode(Crash.Reporting mode)
    {
        if ((Crash.Reporting)Platform.Settings.Get(Platform.Keys.CrashReporting) == mode) return;
        Platform.Settings.Set(Platform.Keys.CrashReporting, (int)mode);
        Bump();
    }

    /// <summary>"Your data on the crash service": the shortened install id, Copy id, and the confirmed right-to-erasure
    /// (§J). Its own component: the erase runs off the UI thread and, on success, the install id has ROTATED — the epoch
    /// below re-renders the card so the new id shows.</summary>
    sealed class CrashServiceCard : Component
    {
        public override Element Render()
        {
            var overlay = UseContext(Overlay.Service);
            var hooks = UseContext(InputHooks.Current);
            var post = UsePost();
            var epoch = UseSignal(0);
            _ = epoch.Value;   // subscribed: a rotated install id re-renders

            string fullId = Crash.InstallId.Peek(Platform.Settings);   // boot ensures it; a render never writes the store
            bool configured = Crash.Uploader.Configured;

            void OnDelete() => Controls.Confirm(overlay, Loc.Get(PvFacts.DeleteMyDataConfirmTitle), Loc.Get(PvFacts.DeleteMyDataConfirmBody),
                Loc.Get(PvFacts.DeleteMyDataConfirmButton), () => _ = Task.Run(async () =>
                {
                    var result = await Crash.Uploader.DeleteRemote(CancellationToken.None).ConfigureAwait(false);
                    post(() =>
                    {
                        if (result.Ok) Notify.Say(PvFacts.DeleteMyDataDone(result.Deleted.ToString(CultureInfo.InvariantCulture)), InfoBarSeverity.Success);
                        else Notify.Say(Loc.Get(PvFacts.DeleteMyDataFailed), InfoBarSeverity.Error);
                        epoch.Value = epoch.Peek() + 1;
                    });
                }));

            return SettingsCard.Create(new SettingsCard.Options
            {
                Header = Loc.Get(PvFacts.YourData),
                Description = PvFacts.YourDataSub(PrivacyRules.ShortInstallId(fullId)),
                HeaderIcon = RowGlyph(Tab.PrivacyDiagnostics, "crashService"),
                Content = HStack(Spacing.S,
                    Button.Subtle(Loc.Get(PvFacts.CopyId), () => hooks.Clipboard?.SetText(fullId), isEnabled: fullId.Length > 0),
                    Button.Standard(Loc.Get(PvFacts.DeleteMyData), OnDelete, isEnabled: configured)),
            });
        }
    }

    static Element PrivacyPolicyCard() => Row(Loc.Get(PvFacts.Policy), Loc.Get(PvFacts.PolicySub), null,
        RowGlyph(Tab.PrivacyDiagnostics, "privacyPolicy"), isClickEnabled: true,
        onClick: static () => OpenUri(Crash.PrivacyUrl), actionIcon: Icons.OpenInNewWindow);

    // ══ 3. CRASH REPORTS › SAVED REPORTS ══════════════════════════════════════════════════════════════════════════════

    /// <summary>ONE expander over <see cref="Crash.Host.Bundles"/>: the send-queue InfoBar first (only while the outbox
    /// is non-empty), then a row per report with a single "…" menu (View · Copy · Send… · Delete), Open folder and Delete
    /// all in the footer. Subscribes the two events that change what it shows — the reports folder and the outbox — so the
    /// page never re-renders for them.</summary>
    sealed class SavedReportsGroup : Component
    {
        readonly Dictionary<string, NodeHandle> _anchors = new(StringComparer.Ordinal);
        OverlayHandle? _menu;

        public override Element Render()
        {
            var overlay = UseContext(Overlay.Service);
            var hooks = UseContext(InputHooks.Current);
            var post = UsePost();
            _ = Crash.Host.ReportsVersion.Value;       // subscribed
            _ = Crash.Uploader.OutboxVersion.Value;    // subscribed
            var bundles = Crash.Host.Bundles();        // at most Crash.Files.KeepBundles; event-driven, never per frame
            var tally = PrivacyRules.Tally(bundles, BundleBytes);
            int queued = Crash.Uploader.QueuedCount();
            bool configured = Crash.Uploader.Configured;

            var items = new List<Element>(bundles.Count + 1);
            if (queued > 0)
                items.Add(ExpanderPanel(InfoBar.Create(InfoBarSeverity.Informational, "",
                    queued == 1 ? Loc.Get(PvReports.QueuedOne) : PvReports.Queued(queued.ToString(CultureInfo.InvariantCulture)),
                    isClosable: false,
                    actionButton: HStack(Spacing.S,
                        Button.Accent(Loc.Get(PvReports.SendNow), static () => Crash.Uploader.Drain(), isEnabled: configured),
                        Button.Standard(Loc.Get(PvReports.Discard), static () => Crash.Uploader.DiscardQueue())))) with { Key = "privacy.reports.queued" });
            foreach (var b in bundles)
                items.Add(ReportRow(b, overlay, hooks, post, configured) with { Key = "crash:" + b.Summary.ReportId });
            PruneAnchors(bundles);

            return SettingsExpander.Create(new SettingsExpander.Options
            {
                Header = Loc.Get(PvReports.Saved),
                Description = tally.Total == 0
                    ? Loc.Get(PvReports.SavedNone)
                    : PvReports.SavedSummary(tally.Sent.ToString(CultureInfo.InvariantCulture),
                        tally.NotSent.ToString(CultureInfo.InvariantCulture), tally.Closed.ToString(CultureInfo.InvariantCulture)),
                HeaderIcon = RowGlyph(Tab.PrivacyDiagnostics, "savedReports"),
                Content = ValueTag(tally.Total == 1
                    ? PvReports.SavedCountOne(PrivacyRules.Mb(tally.Bytes))
                    : PvReports.SavedCount(tally.Total.ToString(CultureInfo.InvariantCulture), PrivacyRules.Mb(tally.Bytes))),
                Items = items,
                ItemsFooter = ExpanderFooter(new BoxEl
                {
                    Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, AlignSelf = FlexAlign.Stretch,
                    Padding = new Edges4(58f, Spacing.S, Spacing.L, Spacing.S),
                    Children =
                    [
                        Caption(PvReports.Retention(Crash.Files.KeepBundles.ToString(CultureInfo.InvariantCulture)))
                            with { Color = Tok.TextTertiary, Grow = 1f, MinWidth = 0f, Wrap = TextWrap.Wrap },
                        Button.Standard(Loc.Get(PvReports.OpenFolder), static () => Diagnostics.OpenFolder(Crash.Files.Root(Crash.Host.LogFolder))),
                        Button.Standard(Loc.Get(PvReports.DeleteAll), () => Controls.Confirm(overlay,
                            Loc.Get(PvReports.DeleteAllConfirmTitle), Loc.Get(PvReports.DeleteAllConfirmBody),
                            Loc.Get(PvReports.DeleteAll), static () => Crash.Host.DeleteAll()), isEnabled: tally.Total > 0),
                    ],
                }),
            });
        }

        /// <summary>A deleted bundle's "…" handle goes; every other row keeps its (still realized) anchor — a realization
        /// callback fires once per node, so clearing them all would strand the survivors' menus.</summary>
        void PruneAnchors(IReadOnlyList<Crash.BundleInfo> bundles)
        {
            if (_anchors.Count <= bundles.Count) return;
            List<string>? stale = null;
            foreach (var id in _anchors.Keys)
            {
                bool live = false;
                for (int i = 0; i < bundles.Count && !live; i++)
                    live = string.Equals(bundles[i].Summary.ReportId, id, StringComparison.Ordinal);
                if (!live) (stale ??= []).Add(id);
            }
            if (stale is null) return;
            foreach (var id in stale) _anchors.Remove(id);
        }

        /// <summary>One report: kind dot · when + short id · kind · what · send state · "…". Not clickable as a whole —
        /// every action is in the menu.</summary>
        Element ReportRow(Crash.BundleInfo b, IOverlayService? overlay, InputHooks hooks, Action<Action> post, bool configured)
        {
            var s = b.Summary;
            string id = s.ReportId;
            return new BoxEl
            {
                Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center, AlignSelf = FlexAlign.Stretch, MinHeight = 44f,
                Padding = new Edges4(58f, Spacing.S, Spacing.L, Spacing.S),
                Children =
                [
                    new BoxEl { Width = 8f, Height = 8f, Corners = Radii.FullAll, Fill = DotFor(s.Kind), Shrink = 0f },
                    new BoxEl
                    {
                        Direction = 1, Gap = 2f, Width = 150f, Shrink = 0f,
                        Children =
                        [
                            new TextEl(b.StampLocal.ToString("g", CultureInfo.CurrentCulture)) { Size = 12f, Color = Tok.TextSecondary },
                            new TextEl(PvReports.ReportId(Crash.ShortId(id))) { Size = 11f, Color = Tok.TextTertiary },
                        ],
                    },
                    new TextEl(Loc.Get(PrivacyRules.KindKey(s.Kind))) { Size = 13f, Weight = 600, Width = 56f, Shrink = 0f },
                    new TextEl(WhatText(s)) { Size = 13f, Color = Tok.TextSecondary, Grow = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    new TextEl(StateText(b)) { Size = 12f, Color = Tok.TextTertiary, Width = 110f, Shrink = 0f },
                    ToolTip.Wrap(IconButton.Create(Icons.More, () => OpenRowMenu(b, overlay, hooks, post, configured), size: ControlSize.Small)
                        with { Shrink = 0f, OnRealized = h => _anchors[id] = h }, Loc.Get(PvReports.RowMenu)),
                ],
            };
        }

        /// <summary>The row's one menu, anchored at its "…" (the same anchored-flyout shape as the history menu and the
        /// video-override manager). Built at open time, so Send… reflects the live send state. A second press closes it.</summary>
        void OpenRowMenu(Crash.BundleInfo b, IOverlayService? overlay, InputHooks hooks, Action<Action> post, bool configured)
        {
            if (overlay is null) return;
            if (_menu is { IsOpen: true } open) { open.Close(); return; }
            string id = b.Summary.ReportId;
            var items = new List<MenuFlyoutItem>(5)
            {
                new(Loc.Get(PvReports.View), Icons.OpenInNewWindow, true, () => ViewReport(b.ReportTxt)),
                new(Loc.Get(PvReports.Copy), Icons.Copy, true, () => CopyReport(b, hooks, post)),
            };
            if (PrivacyRules.CanSend(b.Summary.Kind, b.Send.State, configured))
                items.Add(new(Loc.Get(PvReports.Send), Icons.Forward, true, () => Crash.OpenSendPrompt(overlay, b)));
            items.Add(MenuFlyoutItem.Separator);
            items.Add(new(Loc.Get(PvReports.Delete), Icons.Delete, true, () => Controls.Confirm(overlay,
                Loc.Get(PvReports.DeleteConfirmTitle), Loc.Get(PvReports.DeleteConfirmBody),
                Loc.Get(PvReports.Delete), () => Crash.Host.Delete(b.Dir))));
            var handle = overlay.Open(() => _anchors.TryGetValue(id, out var a) ? a : NodeHandle.Null,
                () => MenuFlyout.Create(items, () => _menu?.Close()),
                FlyoutPlacement.BottomEdgeAlignedRight,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false });
            _menu = handle;
            handle.ClosedAction = () => { if (ReferenceEquals(_menu, handle)) _menu = null; };
        }
    }

    /// <summary>What a bundle holds on disk (the report, the log tail, the dump). An unreadable file counts as nothing.</summary>
    static long BundleBytes(Crash.BundleInfo b)
    {
        long n = 0;
        try
        {
            n += new FileInfo(b.ReportTxt).Length;
            if (File.Exists(b.TailTxt)) n += new FileInfo(b.TailTxt).Length;
            if (b.DumpPath is { } dump && File.Exists(dump)) n += b.DumpBytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn("crash", "reports.size.failed", ex); }
        return n;
    }

    static ColorF DotFor(Crash.Kind kind) => kind switch
    {
        Crash.Kind.Hang => Tok.SystemFillCaution,
        Crash.Kind.UncleanExit => Tok.SystemFillSolidNeutral,
        _ => Tok.SystemFillCritical,
    };

    /// <summary>The row's "what": the hang's seconds, why a "closed" report exists, or the exception type (the exit code
    /// when there is none) with the last route.</summary>
    static string WhatText(Crash.Summary s) => s.Kind switch
    {
        Crash.Kind.Hang => PrivacyRules.HangSeconds(s.ExceptionMessage) is { } seconds
            ? PvReports.HangWhat(seconds.ToString(CultureInfo.InvariantCulture))
            : s.ExceptionMessage.Length > 0 ? s.ExceptionMessage : Loc.Get(PvReports.KindHang),
        Crash.Kind.UncleanExit => Loc.Get(PvReports.ClosedWhat),
        _ => (s.ExceptionType.Length > 0 ? s.ExceptionType : "0x" + s.ExitCode.ToString("X", CultureInfo.InvariantCulture))
             + (s.LastRoute.Length > 0 ? " · " + s.LastRoute : ""),
    };

    /// <summary>"Sent 14:32", "Waiting to send", "Not sent", "Couldn't send", or a dash for a closed-unexpectedly report.</summary>
    static string StateText(Crash.BundleInfo b)
    {
        string? key = PrivacyRules.StateKey(b.Summary.Kind, b.Send.State);
        if (key is null) return "—";
        return b.Send.State == Crash.SendState.Sent
            ? PvReports.StateSent(PrivacyRules.SentTimeLocal(b.Send.SentAtUtc, TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.UtcNow)))
            : Loc.Get(key);
    }

    /// <summary>"View": the raw local report in Notepad.</summary>
    static void ViewReport(string path)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe", "\"" + path + "\"") { UseShellExecute = false })?.Dispose(); }
        catch (Exception ex) { Log.Warn("crash", "reports.view.failed", ex); }
    }

    /// <summary>"Copy": exactly what a send would carry — the scrub rules are read on the UI thread, the scrub and layout
    /// run off it, and the clipboard and the toast come back on it. A failed scrub copies nothing (never an unscrubbed
    /// report).</summary>
    static void CopyReport(Crash.BundleInfo b, InputHooks hooks, Action<Action> post)
    {
        var rules = Crash.Scrubber.RulesNow();   // UI thread: the live account/device seams
        _ = Task.Run(() =>
        {
            string text = Crash.ComposePreview(b, rules);
            if (text.Length == 0) return;   // the scrub failed (logged)
            post(() =>
            {
                hooks.Clipboard?.SetText(text);
                Notify.Say(Loc.Get(PvReports.Copied), InfoBarSeverity.Success, dedupeKey: "crash-copied:" + b.Summary.ReportId);
            });
        });
    }

    // ══ 4. LOGS ═══════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The Log viewer card: opens the viewer page, and its description counts the live session. The counts and
    /// the uptime are bound into the card's description text node through the <see cref="SettingsCard.PartDescription"/>
    /// template part, fed by a 750 ms interval that writes only when the log or the minute moved — so ONE text node
    /// re-renders, the card never remounts, and nothing runs per frame (N12). The component renders once: its render
    /// reads no signal, and the signal is seeded at construction (a render never writes one).</summary>
    sealed class LogViewerCard : Component
    {
        readonly Signal<string> _line = new(Compose());
        readonly TemplateParts _parts = new();
        long _seenVersion = -1, _seenMinute = -1;

        public LogViewerCard() => _parts.Set<TextEl>(SettingsCard.PartDescription, t => t with { Text = _line });

        public override Element Render()
        {
            UseInterval(() =>
            {
                long version = Log.Version, minute = Log.SinceStartMs / 60_000;
                if (version == _seenVersion && minute == _seenMinute) return;
                _seenVersion = version;
                _seenMinute = minute;
                _line.Value = Compose();
            }, 750f);

            return SettingsCard.Create(new SettingsCard.Options
            {
                Header = Loc.Get(PvLogs.Viewer),
                Description = _line.Peek(),   // non-empty, so the card builds the description node the part binds
                HeaderIcon = RowGlyph(Tab.PrivacyDiagnostics, "logViewer"),
                IsClickEnabled = true,
                IsActionIconVisible = true,
                OnClick = static () => Shell.GoTo(new Shell.Route(Shell.RouteKind.Logs)),
                Parts = _parts,
            });
        }

        static string Compose()
        {
            Log.Tally(out int total, out int warnings, out int errors);
            return PvLogs.ViewerSub(Thousands(total), Thousands(warnings), Thousands(errors),
                Diagnostics.LogView.Uptime(TimeSpan.FromMilliseconds(Log.SinceStartMs)));
        }

        static string Thousands(int n) => n.ToString("N0", CultureInfo.InvariantCulture);
    }

    /// <summary>The two detail levels as one expander: what the viewer captures and what reaches the log file. Both are
    /// persisted settings, so the group subscribes <see cref="Platform.SettingsChanged"/> — the viewer's Verbose toggle
    /// writes the same keys and the header tag follows. A combo's items and selection freeze at mount, so each is keyed by
    /// the level VALUE it shows, and the write is posted so the click finishes before the remount.</summary>
    sealed class DetailLevelGroup : Component
    {
        public override Element Render()
        {
            var post = UsePost();
            _ = Platform.SettingsChanged.Value;   // subscribed
            var viewer = Log.MinLevel;
            var file = LogCapturePolicy.EffectiveFileLevel(viewer, Log.FileMinLevel);
            int viewerIndex = Math.Clamp((int)viewer, 0, Diagnostics.LogView.LevelNames.Length - 1);
            int fileIndex = Math.Clamp((int)file, 0, Diagnostics.LogView.LevelNames.Length - 1);

            return SettingsExpander.Create(new SettingsExpander.Options
            {
                Header = Loc.Get(PvLogs.DetailLevel),
                Description = Loc.Get(PvLogs.DetailLevelSub),
                HeaderIcon = RowGlyph(Tab.PrivacyDiagnostics, "detailLevel"),
                Content = ValueTag(PvLogs.DetailLevelValue(Diagnostics.LogView.LevelNames[viewerIndex], Diagnostics.LogView.LevelNames[fileIndex])),
                Items =
                [
                    Item(Loc.Get(PvLogs.InViewer), Loc.Get(PvLogs.InViewerSub),
                        ComboBox.Create(Diagnostics.LogView.LevelNames, new Signal<int>(viewerIndex), width: 160f,
                            onChange: i => post(() => LogCapturePolicy.SetMinLevel(Platform.Settings, (WaveeLogLevel)Math.Clamp(i, 0, 4))))
                            with { Key = "privacy.level.viewer:" + viewerIndex.ToString(CultureInfo.InvariantCulture) }),
                    Item(Loc.Get(PvLogs.InFile), Loc.Get(PvLogs.InFileSub),
                        ComboBox.Create(Diagnostics.LogView.LevelNames, new Signal<int>(fileIndex), width: 160f,
                            onChange: i => post(() => LogCapturePolicy.SetFileLevel(Platform.Settings, (WaveeLogLevel)Math.Clamp(i, 0, 4))))
                            with { Key = "privacy.level.file:" + fileIndex.ToString(CultureInfo.InvariantCulture) }),
                ],
            });
        }
    }

    /// <summary>The writer's rule in one sentence, fed by the writer's own numbers (never hard-coded here), and a door to
    /// the folder.</summary>
    static Element LogFilesCard() => Row(Loc.Get(PvLogs.Files),
        PvLogs.FilesSub(PrivacyRules.WholeMb(Log.MaxFileBytes), Log.RetentionDays.ToString(CultureInfo.InvariantCulture),
            PrivacyRules.WholeMb(Log.RetentionBytes)),
        Button.Standard(Loc.Get(PvLogs.OpenFolder), static () => Diagnostics.OpenFolder(Platform.LogFolder)),
        RowGlyph(Tab.PrivacyDiagnostics, "logFiles"));

    static Element ReportProblemCard() => Row(Loc.Get(PvLogs.Report), Loc.Get(PvLogs.ReportSub), null,
        RowGlyph(Tab.PrivacyDiagnostics, "reportProblem"), isClickEnabled: true,
        onClick: static () => Feedback.Open(Feedback.ReportKind.Bug));

    // ══ 5. TOOLS ══════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A clickable card that opens one diagnostics page (Playback keeps its own door to the same pages).</summary>
    static Element ToolLinkCard(string headerKey, string subKey, string rowId, Shell.RouteKind route)
        => Row(Loc.Get(headerKey), Loc.Get(subKey), null, RowGlyph(Tab.PrivacyDiagnostics, rowId), isClickEnabled: true,
            onClick: () => Shell.GoTo(new Shell.Route(route)));

    /// <summary>The persisted key/subtitle are `dealerArchive` verbatim (CLAUDE.md: no legacy renumbering); the toggle
    /// applies live through <c>RealtimeCaptureHost.OnSettingsChanged</c>, and the two doors sit inside.</summary>
    static Element RealtimeCaptureExpander() => SettingsExpander.Create(new SettingsExpander.Options
    {
        Header = Loc.Get(PvTools.Capture),
        Description = Loc.Get(PvTools.CaptureSub),
        HeaderIcon = RowGlyph(Tab.PrivacyDiagnostics, "realtimeCapture"),
        Content = Toggle(Platform.Keys.DealerArchiveEnabled, afterWrite: static _ => RealtimeCaptureHost.OnSettingsChanged()),
        Items =
        [
            Item(Loc.Get(PvTools.OpenCaptureViewer), Loc.Get(PvTools.OpenCaptureViewerSub), isClickEnabled: true,
                onClick: static () => Shell.GoTo(new Shell.Route(Shell.RouteKind.CaptureDiagnostics))),
            Item(Loc.Get(PvTools.OpenCaptureFolder), Loc.Get(PvTools.OpenCaptureFolderSub),
                Button.Standard(Loc.Get(PvTools.OpenFolder), static () => Diagnostics.OpenFolder(Path.Combine(Platform.LogFolder, "capture")))),
        ],
    }) with { Key = "privacy.capture" };

    // ══ 6. DEVELOPER ══════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>True from the click until the test bundle is written and handed to the uploader (UI thread only). It
    /// guards the GENERATION — one live dump request at a time from here — not the upload: each click is its own report,
    /// and its outcome card is its own (<c>crash:&lt;id&gt;</c>). A signal, so the Developer group's button follows it.</summary>
    static readonly Signal<bool> s_testCrashBusy = new(false);

    /// <summary>Developer mode and the three items it unlocks. The items are PRESENT and greyed while the switch is off
    /// (D2), never composed away. The switch and the FPS overlay go through <see cref="Platform.Developer"/> — the one
    /// persist-then-publish setter — so they apply at once instead of at the next launch; the group reads the same
    /// signals, so it follows a flip made anywhere.</summary>
    sealed class DeveloperGroup : Component
    {
        public override Element Render()
        {
            bool dev = Platform.Developer.Enabled.Value;
            bool fps = Platform.Developer.FpsOverlay.Value;
            bool busy = s_testCrashBusy.Value;
            bool canSend = Crash.Uploader.Configured;

            return SettingsExpander.Create(new SettingsExpander.Options
            {
                Header = Loc.Get(PvDev.Mode),
                Description = Loc.Get(PvDev.ModeSub),
                HeaderIcon = RowGlyph(Tab.PrivacyDiagnostics, "developerMode"),
                Content = ToggleSwitch.Create(new Signal<bool>(dev), onChange: static on =>
                {
                    Platform.Developer.Set(on);
                    Bump();   // the Appearance / Notifications developer-only rows are page-composed
                }, style: SettingsCard.CompactToggleStyle()),
                Items =
                [
                    Item(Loc.Get(PvDev.FpsOverlay), Loc.Get(PvDev.FpsOverlaySub),
                        ToggleSwitch.Create(new Signal<bool>(fps), onChange: static on => Platform.Developer.SetFpsOverlay(on),
                            isEnabled: dev, style: SettingsCard.CompactToggleStyle()),
                        isEnabled: dev, icon: RowGlyph(Tab.PrivacyDiagnostics, "fpsOverlay")),
                    Item(Loc.Get(PvDev.SimulateUpdate), Loc.Get(PvDev.SimulateUpdateSub),
                        Button.Standard(Loc.Get(PvDev.SimulateUpdateButton), static () => Update.Host.SimulateUpdate(), isEnabled: dev),
                        isEnabled: dev, icon: RowGlyph(Tab.PrivacyDiagnostics, "simulateUpdate")),
                    Item(Loc.Get(PvDev.SendTestCrashReport), Loc.Get(canSend ? PvDev.SendTestCrashReportSub : PvDev.SendTestCrashReportUnavailable),
                        Button.Standard(Loc.Get(PvDev.SendTestCrashReportButton), static () => SendTestCrashReport(),
                            isEnabled: dev && canSend && !busy),
                        isEnabled: dev && canSend, icon: RowGlyph(Tab.PrivacyDiagnostics, "sendTestCrashReport")),
                ],
            });
        }
    }

    /// <summary>Developer › "Send a test crash report" (#165): <see cref="Crash.Host.WriteTestReport"/> off the UI thread
    /// (it blocks on the crash handler's dump), then — back on it — the Saved reports bump and the NORMAL send:
    /// <see cref="Crash.Uploader.Enqueue"/> with the dump, whatever the reporting mode (this click is the manual send),
    /// and the real outcome card (<c>Crash.OutcomeToasts</c>) carrying the report's short id.</summary>
    static void SendTestCrashReport()
    {
        if (s_testCrashBusy.Peek() || !Crash.Uploader.Configured) return;
        s_testCrashBusy.Value = true;
        var post = s_post;
        var rules = Crash.Scrubber.RulesNow();   // UI thread: the live account/device tables
        var writing = Notify.Say(Loc.Get(Strings.Crash.TestReportWriting), InfoBarSeverity.Informational,
            dedupeKey: "crash-test-report", durationMs: 15000f);
        _ = Task.Run(() =>
        {
            Crash.BundleInfo? bundle = Crash.Host.WriteTestReport(rules);
            post(() =>
            {
                s_testCrashBusy.Value = false;
                if (bundle is null)
                {
                    writing.Close();
                    Notify.Say(Loc.Get(Strings.Crash.TestReportFailed), InfoBarSeverity.Error, durationMs: 8000f);
                    return;
                }
                Crash.Host.ReportsVersion.Value++;   // Saved reports lists it now
                var outcome = Crash.OutcomeToasts.Track(bundle, queuedShown: false, test: true);
                Crash.Uploader.Enqueue(bundle, includeDump: true, settled: rec =>
                {
                    writing.Close();
                    outcome(rec);
                });
            });
        });
    }

    // ══ 7. SHARED CHROME OF THIS TAB'S EXPANDERS ══════════════════════════════════════════════════════════════════════

    /// <summary>A footer under an expander's items: a hairline above, then the content (the engine puts dividers between
    /// items but none before a footer).</summary>
    static Element ExpanderFooter(Element content) => new BoxEl
    {
        Direction = 1, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
        Children = [new BoxEl { Height = 1f, Fill = Tok.StrokeDividerDefault }, content],
    };

    /// <summary>A tertiary caption aligned with the items' text column (58 DIP from the left edge).</summary>
    static Element FooterNote(string text) => new BoxEl
    {
        Direction = 0, AlignSelf = FlexAlign.Stretch,
        Padding = new Edges4(58f, Spacing.M, Spacing.L, Spacing.M),
        Children = [Caption(text) with { Color = Tok.TextTertiary, Grow = 1f, MinWidth = 0f, Wrap = TextWrap.Wrap }],
    };
}
